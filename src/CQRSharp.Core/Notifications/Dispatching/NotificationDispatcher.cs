using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     The built-in <see cref="INotificationDispatcher" />, one per scope: it routes a notification to the outbox for
///     durable delivery or dispatches it in process, as <see cref="OutboxOptions.Mode" /> decides.
/// </summary>
internal sealed class NotificationDispatcher : INotificationDispatcher
{
    private readonly IDirectNotificationDispatcher _directDispatcher;
    private readonly OutboxOptions _outboxOptions;
    private readonly IServiceProvider _provider;

    /// <param name="provider">The scoped service provider.</param>
    /// <param name="outboxOptions">The configuration options for the outbox.</param>
    /// <param name="directDispatcher">The dispatcher for sending notifications immediately.</param>
    public NotificationDispatcher(
        IServiceProvider provider,
        IOptions<OutboxOptions> outboxOptions,
        IDirectNotificationDispatcher directDispatcher)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _outboxOptions = outboxOptions?.Value ?? throw new ArgumentNullException(nameof(outboxOptions));
        _directDispatcher = directDispatcher ?? throw new ArgumentNullException(nameof(directDispatcher));
    }

    /// <summary>
    ///     Publishes a notification.
    ///     While the configured <see cref="OutboxMode" /> routes to the outbox, a notification the registered
    ///     <see cref="INotificationSerializer" /> names is routed to it for deferred, durable delivery; any other
    ///     notification, and every notification while the outbox is off, is dispatched immediately to its handlers. Inside a
    ///     running request of this scope, or an outbox delivery (which owns what its handler publishes the same way), it is
    ///     buffered and stored when that succeeds (with its unit of work's commit when it runs in one); anywhere else
    ///     nothing would settle a buffer, so it is written straight to the <see cref="IOutboxStore" />.
    /// </summary>
    /// <typeparam name="TNotification">The type of the notification.</typeparam>
    /// <param name="notification">The notification instance to publish.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the completion of the dispatch action.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the notification goes straight to the store but no <see cref="IOutboxStore" /> is registered.
    /// </exception>
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        // The unit of work is only consulted in Transactional mode. With the outbox disabled (the default) this is the
        // hot path of every request's lifecycle notifications, so it must not pay for a DI lookup it never uses.
        var useOutbox = _outboxOptions.Mode switch
        {
            OutboxMode.Enabled => true,
            OutboxMode.Transactional => _provider.GetService<IUnitOfWork>() is { HasActiveTransaction: true },
            _ => false
        };

        if (!useOutbox) return PublishInProcess(notification, cancellationToken);

        // The serializer that will store the notification is the one that decides whether it can be stored: a name from
        // it is the opt-in to the outbox, which keeps framework notifications (and anything it cannot serialize)
        // in-process. Asked per provider, never cached statically: two hosts in one process may register different
        // modules or a different serializer.
        var serializer = _provider.GetService<INotificationSerializer>();
        if (serializer is null || !serializer.TryGetNotificationName(notification.GetType(), out _))
            return PublishInProcess(notification, cancellationToken);

        // Buffered only inside a running request of this scope, which then settles it. A publish from anywhere else (a
        // controller, a hosted service, a stream's consumer between items) has no request here to settle it, so it goes
        // straight to the store rather than into a buffer nothing would ever store.
        if (_provider.GetService<ScopedOutbox>() is { } outbox && outbox.TryBuffer(notification))
            return Task.CompletedTask;

        return RequestOutboxScope.StoreAsync(_provider, [notification], cancellationToken);
    }

    // In-process delivery follows one rule for every publish (see DirectNotificationDispatcher): the handlers and the
    // behaviors of the notification's runtime type, whatever type it was published as.
    private Task PublishInProcess<TNotification>(TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
        => _directDispatcher.Publish(notification, cancellationToken);
}
