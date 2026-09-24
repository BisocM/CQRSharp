namespace CQRSharp.Core.Outbox;

/// <summary>
///     What one owner of the scope's outbox buffer published, settled with the commit of the unit of work it ran in: a
///     store that joins the transaction is written just before the commit, inside it, so the messages commit and roll
///     back with the work they announce; any other store is written right after the commit succeeded, so a commit that
///     fails publishes nothing. The unit-of-work behaviors (for a request) and the outbox processor (for a delivery it
///     runs in a transaction of its own) both settle through it, so the two cannot drift.
/// </summary>
internal sealed class OutboxCommit
{
    private static readonly OutboxCommit Nothing = new(null, Array.Empty<INotification>(), storeAfterCommit: false, storedBeforeCommit: 0);

    private readonly OutboxWriter? _writer;
    private readonly bool _storeAfterCommit;
    private readonly int _storedBeforeCommit;

    private OutboxCommit(OutboxWriter? writer, IReadOnlyList<INotification> notifications, bool storeAfterCommit, int storedBeforeCommit)
    {
        _writer = writer;
        Notifications = notifications;
        _storeAfterCommit = storeAfterCommit;
        _storedBeforeCommit = storedBeforeCommit;
    }

    /// <summary>The notifications this commit settles, in publication order.</summary>
    public IReadOnlyList<INotification> Notifications { get; }

    /// <summary>
    ///     Takes what <paramref name="owner" /> settles out of <paramref name="outbox" /> and, when the store joins the
    ///     open transaction, stores it now. Called with the transaction open, right before its commit: a failure here is a
    ///     failure of the commit, and the notifications go with the work its rollback discards.
    /// </summary>
    /// <param name="outbox">The scope's buffer, or <see langword="null" /> when the outbox services are not registered.</param>
    /// <param name="owner">The owner whose notifications the commit settles (the request's, or the delivery's).</param>
    /// <param name="services">The scope the store and the serializer are resolved from.</param>
    /// <param name="cancellationToken">The committing caller's token.</param>
    public static async Task<OutboxCommit> PrepareAsync(
        ScopedOutbox? outbox,
        OutboxOwner? owner,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (outbox is null || owner is null) return Nothing;

        var notifications = outbox.DrainOwned(owner);
        if (notifications.Count == 0) return Nothing;

        var writer = OutboxWriter.Resolve(services);
        if (!writer.Store.JoinsUnitOfWork)
            return new OutboxCommit(writer, notifications, storeAfterCommit: true, storedBeforeCommit: 0);

        var stored = await writer.StoreAsync(notifications, cancellationToken).ConfigureAwait(false);
        return new OutboxCommit(writer, notifications, storeAfterCommit: false, stored);
    }

    /// <summary>
    ///     The commit succeeded: stores the notifications into a store that did not join it, then wakes the processor
    ///     when anything was stored, since only now are the messages visible to it. Called once. Never throws: the work is
    ///     committed and stands whatever happens here, a failed store cannot be undone by a rollback, and rethrowing would
    ///     have a retry run the committed work again. The failure is returned for the caller to report; the notifications
    ///     it names are lost.
    /// </summary>
    /// <returns>The store's failure, or <see langword="null" /> when the notifications are stored.</returns>
    public async Task<Exception?> CompleteAsync()
    {
        var stored = _storedBeforeCommit;
        if (_storeAfterCommit)
            try
            {
                // No caller token: the work is done, and a caller that gives up now must not lose its notifications.
                stored = await _writer!.StoreAsync(Notifications, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ex;
            }

        if (stored > 0) _writer!.Signal();
        return null;
    }
}
