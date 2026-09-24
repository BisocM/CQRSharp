namespace CQRSharp.Core.Outbox;

/// <summary>
///     The DI scope's buffer of outbox-bound notifications published while one of the scope's requests runs, held until
///     that request settles: stored when it succeeds, discarded when it fails.
/// </summary>
/// <remarks>
///     <para>
///         Settlement follows ownership, never a scope-wide count. Each running request of the scope (and each outbox
///         delivery, which the processor runs as one) is registered here with its <see cref="OutboxOwner" />, and every
///         entry remembers the owner that was current when it was published. A request settles what it buffered and what
///         the requests nested in it buffered once they finished, and nothing else: requests that run side by side in one
///         scope (<c>Task.WhenAll</c>, a request sent while a stream is being enumerated, a fire-and-forget send) settle
///         independently, and a nested request that is still running when its caller settles keeps its own entries until
///         it settles itself.
///     </para>
///     <para>
///         A publish is buffered only while it happens inside a running request of <em>this</em> scope. Anything else
///         (a controller, a hosted service, a stream's consumer between items, a request running in another scope) has
///         no request here to settle it, so the dispatcher writes it straight to the store instead.
///     </para>
/// </remarks>
internal sealed class ScopedOutbox
{
    // One gate for both collections: the handlers of a Parallel publish strategy share the request's scope, so they
    // buffer concurrently, while the executor and the unit of work settle entries around them, and the decision to
    // buffer must see the same set of running requests the settlement does.
    private readonly object _gate = new();
    private readonly List<(INotification Notification, OutboxOwner Owner)> _entries = new();

    // Owners are compared by reference (OutboxOwner has no value equality).
    private readonly HashSet<OutboxOwner> _running = new();

    /// <summary>The number of buffered notifications.</summary>
    internal int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>
    ///     Starts a request in this scope: enters a new owner nested in the current one and registers it as running.
    ///     Called synchronously from the async method that runs the request, so the owner stays current for it.
    /// </summary>
    public OutboxOwner BeginRequest()
    {
        var owner = OutboxOwner.Enter();
        lock (_gate) _running.Add(owner);
        return owner;
    }

    /// <summary>
    ///     Buffers <paramref name="notification" /> when it is published inside a running request of this scope, and
    ///     reports whether it did. Decided under the same gate as settlement, so a request cannot settle between the
    ///     decision and the add and leave the entry behind.
    /// </summary>
    public bool TryBuffer(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var current = OutboxOwner.Current;
        if (current is null) return false;

        lock (_gate)
        {
            for (var owner = current; owner is not null; owner = owner.Parent)
                if (_running.Contains(owner))
                {
                    _entries.Add((notification, current));
                    return true;
                }

            return false;
        }
    }

    /// <summary>
    ///     The request of <paramref name="owner" /> succeeded. When it is nested in another running request of this
    ///     scope, its entries are left for that request to settle; otherwise they are removed and returned for storing.
    /// </summary>
    public IReadOnlyList<INotification> CompleteRequest(OutboxOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            _running.Remove(owner);
            for (var ancestor = owner.Parent; ancestor is not null; ancestor = ancestor.Parent)
                if (_running.Contains(ancestor))
                    return Array.Empty<INotification>();

            return TakeSettledBy(owner);
        }
    }

    /// <summary>The request of <paramref name="owner" /> failed: what it settles describes work that did not happen.</summary>
    public void AbandonRequest(OutboxOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            _running.Remove(owner);
            _entries.RemoveAll(e => IsSettledBy(e.Owner, owner));
        }
    }

    /// <summary>
    ///     Removes and returns what <paramref name="owner" /> settles: its own entries and those of the requests nested
    ///     in it that already finished, never an enclosing request's, a sibling's, or a still-running nested request's.
    /// </summary>
    public IReadOnlyList<INotification> DrainOwned(OutboxOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate) return TakeSettledBy(owner);
    }

    /// <summary>Discards what <paramref name="owner" /> settles (see <see cref="DrainOwned" />): the work did not happen.</summary>
    public void DiscardOwned(OutboxOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate) _entries.RemoveAll(e => IsSettledBy(e.Owner, owner));
    }

    private IReadOnlyList<INotification> TakeSettledBy(OutboxOwner owner)
    {
        List<INotification>? taken = null;
        for (var i = 0; i < _entries.Count; i++)
            if (IsSettledBy(_entries[i].Owner, owner))
                (taken ??= new List<INotification>()).Add(_entries[i].Notification);

        if (taken is null) return Array.Empty<INotification>();
        _entries.RemoveAll(e => IsSettledBy(e.Owner, owner));
        return taken;
    }

    // An entry belongs to the settling owner when the owner is on its chain and no running request of this scope sits
    // between them: that request has not finished, and it settles the entry itself.
    private bool IsSettledBy(OutboxOwner entryOwner, OutboxOwner settler)
    {
        for (var owner = entryOwner; owner is not null; owner = owner.Parent)
        {
            if (ReferenceEquals(owner, settler)) return true;
            if (_running.Contains(owner)) return false;
        }

        return false;
    }
}

/// <summary>
///     The request (or the handler attempt) a buffered notification belongs to. Flows with the async execution of the
///     request, so a publish is attributed to whatever is running when it happens; a nested request's owner is a child
///     of its caller's.
/// </summary>
internal sealed class OutboxOwner
{
    private static readonly AsyncLocal<OutboxOwner?> CurrentOwner = new();

    private OutboxOwner(OutboxOwner? parent) => Parent = parent;

    public OutboxOwner? Parent { get; }

    /// <summary>The owner of whatever runs now, or null outside any request.</summary>
    public static OutboxOwner? Current => CurrentOwner.Value;

    /// <summary>
    ///     Starts a new owner nested in the current one. The async method that calls this sees it as current until it
    ///     returns; an async method's changes to the flow never leak back to its caller.
    /// </summary>
    public static OutboxOwner Enter()
    {
        var owner = new OutboxOwner(CurrentOwner.Value);
        CurrentOwner.Value = owner;
        return owner;
    }

    /// <summary>Makes <paramref name="owner" /> current for the rest of the calling async method.</summary>
    public static void Resume(OutboxOwner owner) => CurrentOwner.Value = owner;
}
