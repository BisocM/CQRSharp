using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Outbox;

/// <summary>
///     One request's hold on the scope's <see cref="ScopedOutbox" />: registered as running when the request starts,
///     and settled when it ends. This is the fallback that stores whatever a unit of work did not already take (a
///     request without one, or one taking part in a transaction someone else owns), so a buffered notification is never
///     silently dropped. The outbox processor holds one for each delivery, which owns what its handler publishes the
///     same way.
/// </summary>
internal readonly struct RequestOutboxScope
{
    private readonly IServiceProvider _provider;
    private readonly ScopedOutbox _outbox;
    private readonly OutboxOwner _owner;

    private RequestOutboxScope(IServiceProvider provider, ScopedOutbox outbox, OutboxOwner owner)
    {
        _provider = provider;
        _outbox = outbox;
        _owner = owner;
    }

    /// <summary>What the request buffers, its behaviors included, is attributed to this owner.</summary>
    public OutboxOwner Owner => _owner;

    /// <summary>
    ///     Starts the request in the scope's buffer and makes its owner current for the calling async method;
    ///     <c>null</c> when the outbox services are not registered in this scope.
    /// </summary>
    public static RequestOutboxScope? Begin(IServiceProvider provider)
    {
        var outbox = provider.GetService<ScopedOutbox>();
        return outbox is null ? null : new RequestOutboxScope(provider, outbox, outbox.BeginRequest());
    }

    /// <summary>
    ///     The request succeeded: unless it is nested in another running request of the scope (which then settles its
    ///     notifications along with its own), store what it buffered and no unit of work already took.
    /// </summary>
    public Task CompleteAsync()
    {
        var settled = _outbox.CompleteRequest(_owner);
        if (settled.Count == 0) return Task.CompletedTask;

        // The handler's work is done and these notifications are its durable side effects: they are stored under no
        // caller token, so a request abandoned at this instant cannot lose them while the work they announce stands.
        return StoreAsync(_provider, settled, CancellationToken.None);
    }

    /// <summary>
    ///     The request's transaction is about to commit: takes what it buffered so far into the commit (see
    ///     <see cref="OutboxCommit" />). Called with the transaction open; what the request buffers afterwards is left for
    ///     <see cref="CompleteAsync" />.
    /// </summary>
    public Task<OutboxCommit> PrepareCommitAsync(CancellationToken cancellationToken)
        => OutboxCommit.PrepareAsync(_outbox, _owner, _provider, cancellationToken);

    /// <summary>
    ///     The request failed, returned a failed result, or its stream was abandoned: what it buffered describes work
    ///     that did not happen, so it is discarded. Nothing another request of the scope buffered is touched.
    /// </summary>
    public void Abandon() => _outbox.AbandonRequest(_owner);

    /// <summary>Writes notifications straight to the durable store (no buffering).</summary>
    public static async Task StoreAsync(IServiceProvider provider, IReadOnlyList<INotification> notifications, CancellationToken cancellationToken)
    {
        var writer = OutboxWriter.Resolve(provider);

        // Nothing here commits around the write, so the processor is woken at once. A write into a transaction the
        // caller owns only becomes visible at the caller's commit; the poll after it picks the messages up.
        if (await writer.StoreAsync(notifications, cancellationToken).ConfigureAwait(false) > 0)
            writer.Signal();
    }
}
