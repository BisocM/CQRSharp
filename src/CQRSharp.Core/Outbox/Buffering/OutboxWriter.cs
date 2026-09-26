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
            throw ServicesMissing();

        return new OutboxWriter(store, serializer, subscriptions, provider.GetService<TimeProvider>() ?? TimeProvider.System, provider.GetService<IOutboxSignal>());
    }

    /// <summary>The <c>CQRCONF001</c> failure: an outbox mode is active, but a service it stores through is not registered.</summary>
    public static InvalidOperationException ServicesMissing()
        => new("CQRSharp configuration error CQRCONF001: outbox mode is active, but IOutboxStore, INotificationSerializer or " +
               "INotificationSubscriptionRegistry is not registered. " +
               "Select a store inside UseOutbox(...) (UseInMemoryStore, UseRedis, UseEntityFrameworkCore<TContext>) " +
               "and ensure AddCqrsGenerated(...) ran during startup.");

    /// <summary>
    ///     Stores one message per (notification, subscribed handler), in one call to the store; the store is not called at
    ///     all when no handler subscribes to any of them. Returns whether any stored message is due now, which is when the
    ///     processor is worth waking: a scheduled message is claimed on a poll after its due time. The processor is not
    ///     woken here, since only the caller knows when the messages become visible (<see cref="Signal" />).
    /// </summary>
    public async Task<bool> StoreAsync(IReadOnlyList<OutboxEntry> entries, CancellationToken cancellationToken)
    {
        var messages = OutboxMessageFactory.Create(entries, _serializer, _subscriptions, _timeProvider);
        if (messages.Length == 0) return false;

        await Store.StoreAsync(messages, cancellationToken).ConfigureAwait(false);
        return Array.Exists(messages, m => m.NextRetryAt is null);
    }

    /// <summary>Wakes the outbox processor: stored messages are visible now.</summary>
    public void Signal() => _signal?.Signal();
}
