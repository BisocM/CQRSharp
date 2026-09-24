using CQRSharp.Core.Notifications;
using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Outbox;

/// <summary>
///     The services that turn buffered notifications into stored outbox messages, resolved together from one scope:
///     shared by every component that stores them (a direct publish, a request that settles, a unit of work that
///     commits), so each fails with the same configuration error and stamps messages the same way.
/// </summary>
internal sealed class OutboxWriter
{
    private readonly INotificationSerializer _serializer;
    private readonly INotificationSubscriptionRegistry _subscriptions;
    private readonly TimeProvider _timeProvider;
    private readonly IOutboxSignal? _signal;

    private OutboxWriter(
        IOutboxStore store,
        INotificationSerializer serializer,
        INotificationSubscriptionRegistry subscriptions,
        TimeProvider timeProvider,
        IOutboxSignal? signal)
    {
        Store = store;
        _serializer = serializer;
        _subscriptions = subscriptions;
        _timeProvider = timeProvider;
        _signal = signal;
    }

    /// <summary>The scope's outbox store.</summary>
    public IOutboxStore Store { get; }

    /// <summary>Resolves the writer from <paramref name="provider" />.</summary>
    /// <exception cref="InvalidOperationException">The store, the serializer or the subscription registry is not registered.</exception>
    public static OutboxWriter Resolve(IServiceProvider provider)
    {
        var store = provider.GetService<IOutboxStore>();
        var serializer = provider.GetService<INotificationSerializer>();
        var subscriptions = provider.GetService<INotificationSubscriptionRegistry>();
        if (store is null || serializer is null || subscriptions is null)
            throw new InvalidOperationException(
                "Outbox mode is active, but IOutboxStore, INotificationSerializer or INotificationSubscriptionRegistry is not registered. " +
                "Select a store inside UseOutbox(...) (UseInMemoryStore, UseRedis, UseEntityFrameworkCore<TContext>) " +
                "and ensure AddCqrsGenerated(...) ran during startup.");

        return new OutboxWriter(store, serializer, subscriptions, provider.GetService<TimeProvider>() ?? TimeProvider.System, provider.GetService<IOutboxSignal>());
    }

    /// <summary>
    ///     Stores one message per (notification, subscribed handler), in one call to the store. Returns how many were
    ///     stored; zero when no handler subscribes to any of them, and the store is then not called at all. The processor
    ///     is not woken here, since only the caller knows when they become visible (<see cref="Signal" />).
    /// </summary>
    public async Task<int> StoreAsync(IReadOnlyList<INotification> notifications, CancellationToken cancellationToken)
    {
        var messages = OutboxMessageFactory.Create(notifications, _serializer, _subscriptions, _timeProvider);
        if (messages.Length == 0) return 0;

        await Store.StoreAsync(messages, cancellationToken).ConfigureAwait(false);
        return messages.Length;
    }

    /// <summary>Wakes the outbox processor: stored messages are visible now.</summary>
    public void Signal() => _signal?.Signal();
}
