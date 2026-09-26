using CQRSharp.Core.Notifications;
using CQRSharp.Core.Transports;
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
    private readonly NotificationTransportRegistry _transports;
    private readonly TimeProvider _timeProvider;
    private readonly IOutboxSignal? _signal;

    private OutboxWriter(
        IOutboxStore store,
        INotificationSerializer serializer,
        INotificationSubscriptionRegistry subscriptions,
        NotificationTransportRegistry transports,
        TimeProvider timeProvider,
        IOutboxSignal? signal)
    {
        Store = store;
        Serializer = serializer;
        Subscriptions = subscriptions;
        _transports = transports;
        _timeProvider = timeProvider;
        _signal = signal;
    }

    /// <summary>The scope's outbox store.</summary>
    public IOutboxStore Store { get; }

    /// <summary>The serializer the messages are named and written by.</summary>
    public INotificationSerializer Serializer { get; }

    /// <summary>The subscriptions the messages are addressed by.</summary>
    public INotificationSubscriptionRegistry Subscriptions { get; }

    /// <summary>Resolves the writer from <paramref name="provider" />.</summary>
    /// <exception cref="InvalidOperationException">
    ///     The store, the serializer or the subscription registry is not registered, or a notification transport's name
    ///     clashes (<c>CQRCONF015</c>), so a stored message could reach the wrong recipient.
    /// </exception>
    public static OutboxWriter Resolve(IServiceProvider provider)
    {
        var store = provider.GetService<IOutboxStore>();
        var serializer = provider.GetService<INotificationSerializer>();
        var subscriptions = provider.GetService<INotificationSubscriptionRegistry>();
        if (store is null || serializer is null || subscriptions is null)
            throw ServicesMissing();

        var transports = provider.GetService<NotificationTransportRegistry>() ?? NotificationTransportRegistry.Empty;
        if (transports.Failure is { } failure)
            throw new InvalidOperationException(failure);

        return new OutboxWriter(
            store,
            serializer,
            subscriptions,
            transports,
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<IOutboxSignal>());
    }

    /// <summary>The <c>CQRCONF001</c> failure: an outbox mode is active, but a service it stores through is not registered.</summary>
    public static InvalidOperationException ServicesMissing()
        => new("CQRSharp configuration error CQRCONF001: outbox mode is active, but IOutboxStore, INotificationSerializer or " +
               "INotificationSubscriptionRegistry is not registered. " +
               "Select a store inside UseOutbox(...) (UseInMemoryStore, UseRedis, UseEntityFrameworkCore<TContext>) " +
               "and ensure AddCqrsGenerated(...) ran during startup.");

    /// <summary>
    ///     Stores one message per (notification, subscribed handler) and per (notification, forwarding transport), in one
    ///     call to the store; the store is not called at all when there is no recipient for any of them. Returns whether any
    ///     stored message is due now, which is when the processor is worth waking: a scheduled message is claimed on a poll
    ///     after its due time. The processor is not woken here, since only the caller knows when the messages become visible
    ///     (<see cref="Signal" />).
    /// </summary>
    public async Task<bool> StoreAsync(IReadOnlyList<OutboxEntry> entries, CancellationToken cancellationToken)
    {
        var messages = OutboxMessageFactory.Create(entries, Serializer, Subscriptions, _transports, _timeProvider);
        if (messages.Length == 0) return false;

        await Store.StoreAsync(messages, cancellationToken).ConfigureAwait(false);
        return Array.Exists(messages, m => m.NextRetryAt is null);
    }

    /// <summary>
    ///     Stores notifications received through a transport: one message per (notification, subscribed handler), never one
    ///     for a transport, so what was received is not forwarded back out. Returns how many messages were stored; the
    ///     caller wakes the processor once they are visible.
    /// </summary>
    public async Task<int> StoreReceivedAsync(IReadOnlyList<OutboxEntry> entries, CancellationToken cancellationToken)
    {
        var messages = OutboxMessageFactory.Create(entries, Serializer, Subscriptions, transports: null, _timeProvider);
        if (messages.Length == 0) return 0;

        await Store.StoreAsync(messages, cancellationToken).ConfigureAwait(false);
        return messages.Length;
    }

    /// <summary>Wakes the outbox processor: stored messages are visible now.</summary>
    public void Signal() => _signal?.Signal();
}
