using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Background.Outbox;

/// <summary>
///     Scoped marker recording whether a request is currently executing in this DI scope — i.e. whether something
///     is guaranteed to flush the scoped <see cref="IOutbox" />. While active, outbox-bound notifications are buffered
///     and persisted when the request succeeds; outside a request nothing would ever drain the buffer, so the
///     notification dispatcher writes straight to the <see cref="IOutboxStore" /> instead.
/// </summary>
internal sealed class OutboxBufferingState
{
    private int _depth;

    public bool IsActive => Volatile.Read(ref _depth) > 0;

    public void Enter() => Interlocked.Increment(ref _depth);

    /// <summary>Leaves one level; returns <c>true</c> when the outermost request just ended.</summary>
    public bool Exit() => Interlocked.Decrement(ref _depth) == 0;
}

/// <summary>
///     The request-level owner of the scoped outbox: the fallback that persists whatever a unit-of-work behavior did
///     not already drain, so a buffered notification is never silently dropped when the scope ends.
/// </summary>
internal readonly struct RequestOutboxScope
{
    private readonly IServiceProvider _provider;
    private readonly OutboxBufferingState _state;
    private readonly IOutbox _outbox;

    private RequestOutboxScope(IServiceProvider provider, OutboxBufferingState state, IOutbox outbox)
    {
        _provider = provider;
        _state = state;
        _outbox = outbox;
    }

    /// <summary>Enters the request; <c>null</c> when the outbox services are not registered in this scope.</summary>
    public static RequestOutboxScope? Begin(IServiceProvider provider)
    {
        var state = provider.GetService<OutboxBufferingState>();
        var outbox = provider.GetService<IOutbox>();
        if (state is null || outbox is null) return null;

        state.Enter();
        return new RequestOutboxScope(provider, state, outbox);
    }

    /// <summary>
    ///     The request succeeded. When it was the outermost request in the scope, persist any notification a
    ///     unit-of-work behavior did not already drain (non-transactional requests, or a caller-owned transaction).
    /// </summary>
    public Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (!_state.Exit()) return Task.CompletedTask;

        var leftovers = _outbox.Drain();
        return leftovers.Count == 0
            ? Task.CompletedTask
            : StoreAsync(_provider, leftovers, cancellationToken);
    }

    /// <summary>The request failed: a notification it buffered describes work that did not happen, so discard it.</summary>
    public void Abandon()
    {
        if (_state.Exit()) _outbox.Drain();
    }

    /// <summary>Writes notifications straight to the durable store (no buffering).</summary>
    public static Task StoreAsync(IServiceProvider provider, IReadOnlyList<INotification> notifications, CancellationToken cancellationToken)
    {
        var store = provider.GetService<IOutboxStore>();
        var serializer = provider.GetService<INotificationSerializer>();
        if (store is null || serializer is null)
            throw new InvalidOperationException(
                "Outbox mode is active, but IOutboxStore or INotificationSerializer is not registered. " +
                "Select a store inside UseOutbox(...) (UseInMemoryStore, UseRedis, UseEntityFrameworkCore<TContext>) " +
                "and ensure AddCqrsGenerated(...) ran during startup.");

        var timeProvider = provider.GetService<TimeProvider>() ?? TimeProvider.System;
        return store.StoreAsync(OutboxMessageFactory.Create(notifications, serializer, timeProvider), cancellationToken);
    }
}
