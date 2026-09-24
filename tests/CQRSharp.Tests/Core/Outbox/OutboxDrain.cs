using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Tells a test when the outbox processor has done everything it can at the current instant, from the claims it
///     makes. A container observed through <see cref="Observe" /> routes every claim through a store that reports to this
///     drain. A claim that comes back empty is the moment the processor has nothing left to do: it claims again only once
///     every delivery of the batch before has finished and been recorded, and then it waits for its signal or its clock.
///     Nothing here reads a clock; a processor that never gets there leaves the wait to the test run's hang detection.
/// </summary>
internal sealed class OutboxDrain
{
    private readonly object _gate = new();
    private readonly List<(long After, TaskCompletionSource Drained)> _drainWaiters = [];
    private readonly List<(int Count, TaskCompletionSource Reached)> _emptyClaimWaiters = [];
    private long _claimsBegun;
    private int _emptyClaims;

    /// <summary>
    ///     Routes the container's <see cref="IOutboxStore" /> — whichever is registered, with its lifetime — through a
    ///     store that reports every claim to the container's <see cref="OutboxDrain" />. Call it once the store is
    ///     registered, that is after <c>AddCqrsGenerated</c>.
    /// </summary>
    public static IServiceCollection Observe(IServiceCollection services)
    {
        var registered = services.Single(d => d.ServiceType == typeof(IOutboxStore) && !d.IsKeyedService);
        services.Remove(registered);
        services.AddSingleton<OutboxDrain>();
        services.Add(ServiceDescriptor.Describe(
            typeof(IOutboxStore),
            provider => new ObservedStore(
                Resolve(provider, registered),
                provider.GetRequiredService<OutboxDrain>(),
                ownsInner: registered.ImplementationInstance is null),
            registered.Lifetime));
        return services;
    }

    /// <summary>The store an observed container's <see cref="IOutboxStore" /> reports for.</summary>
    public static IOutboxStore Unwrap(IOutboxStore store) => store is ObservedStore observed ? observed.Inner : store;

    /// <summary>Completes once a claim that begins after this call comes back empty.</summary>
    public Task NextEmptyClaimAsync()
    {
        lock (_gate)
        {
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainWaiters.Add((_claimsBegun, drained));
            return drained.Task;
        }
    }

    /// <summary>Completes once <paramref name="count" /> claims in all have come back empty, counting those already made.</summary>
    public Task EmptyClaimsAsync(int count)
    {
        lock (_gate)
        {
            if (_emptyClaims >= count) return Task.CompletedTask;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _emptyClaimWaiters.Add((count, reached));
            return reached.Task;
        }
    }

    private long BeginClaim()
    {
        lock (_gate) return ++_claimsBegun;
    }

    private void EndClaim(long claim, bool empty)
    {
        if (!empty) return;

        List<TaskCompletionSource> completed = [];
        lock (_gate)
        {
            _emptyClaims++;
            completed.AddRange(_drainWaiters.Where(w => w.After < claim).Select(w => w.Drained));
            _drainWaiters.RemoveAll(w => w.After < claim);
            completed.AddRange(_emptyClaimWaiters.Where(w => w.Count <= _emptyClaims).Select(w => w.Reached));
            _emptyClaimWaiters.RemoveAll(w => w.Count <= _emptyClaims);
        }

        foreach (var waiter in completed) waiter.TrySetResult();
    }

    private static IOutboxStore Resolve(IServiceProvider provider, ServiceDescriptor registered)
        => registered.ImplementationInstance as IOutboxStore
           ?? registered.ImplementationFactory?.Invoke(provider) as IOutboxStore
           ?? (IOutboxStore)ActivatorUtilities.CreateInstance(provider, registered.ImplementationType!);

    // Disposes the store underneath when the container created it, as the container would have without the observer.
    private sealed class ObservedStore(IOutboxStore inner, OutboxDrain drain, bool ownsInner) : IOutboxStore, IDisposable, IAsyncDisposable
    {
        public IOutboxStore Inner => inner;

        public void Dispose()
        {
            if (ownsInner && inner is IDisposable disposable) disposable.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            if (!ownsInner) return default;
            if (inner is IAsyncDisposable asyncDisposable) return asyncDisposable.DisposeAsync();
            Dispose();
            return default;
        }

        public bool JoinsUnitOfWork => inner.JoinsUnitOfWork;

        public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimPendingAsync(int batchSize, CancellationToken cancellationToken)
        {
            var claim = drain.BeginClaim();
            var batch = await inner.ClaimPendingAsync(batchSize, cancellationToken);
            drain.EndClaim(claim, batch.Count == 0);
            return batch;
        }

        public Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken) => inner.StoreAsync(messages, cancellationToken);
        public Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken) => inner.RenewAsync(claim, cancellationToken);
        public Task<bool> MarkAsProcessedAsync(OutboxClaim claim, CancellationToken cancellationToken) => inner.MarkAsProcessedAsync(claim, cancellationToken);
        public Task<int> IncrementAttemptAsync(OutboxClaim claim, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken) => inner.IncrementAttemptAsync(claim, error, nextRetryAt, cancellationToken);
        public Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken) => inner.MarkAsFailedAsync(claim, error, cancellationToken);
        public Task<bool> DeferAsync(OutboxClaim claim, DateTime notBefore, string? reason, CancellationToken cancellationToken) => inner.DeferAsync(claim, notBefore, reason, cancellationToken);
        public Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken) => inner.ReleaseAsync(claims, cancellationToken);
        public Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken) => inner.GetBacklogAsync(cancellationToken);
        public Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int limit, CancellationToken cancellationToken) => inner.GetDeadLettersAsync(limit, cancellationToken);
        public Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken) => inner.RequeueAsync(messageId, cancellationToken);
        public Task<int> PurgeDeadLettersAsync(DateTime failedBefore, CancellationToken cancellationToken) => inner.PurgeDeadLettersAsync(failedBefore, cancellationToken);
    }
}
