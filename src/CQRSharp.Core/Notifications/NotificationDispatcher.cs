using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Core.Background.Outbox;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     A sophisticated notification dispatcher that supports both direct, in-process dispatching
///     and a deferred outbox pattern. Its behavior is determined by <see cref="OutboxOptions" />.
///     This service should be registered with a scoped lifetime.
/// </summary>
public sealed class NotificationDispatcher : INotificationDispatcher
{
    private readonly IDirectNotificationDispatcher _directDispatcher;
    private readonly OutboxOptions _outboxOptions;
    private readonly IServiceProvider _provider;

    /// <summary>
    ///     Initializes a new instance of the <see cref="NotificationDispatcher" /> class.
    /// </summary>
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
    ///     Whether an in-process publish ends in the built-in handler/behavior resolution. Only then can "no handler and
    ///     no notification behavior is registered" be taken to mean a publish has no observer at all.
    /// </summary>
    internal bool DispatchesThroughBuiltInPipeline => _directDispatcher is DirectNotificationDispatcher;

    /// <summary>
    ///     Publishes a notification.
    ///     Depending on the configured <see cref="OutboxMode" />, the notification will either be
    ///     dispatched immediately to its handlers or routed to the outbox for deferred, durable delivery. Inside a
    ///     request it is buffered in the scoped <see cref="IOutbox" /> and persisted when the request succeeds
    ///     (atomically with the transaction when a unit of work owns one); outside a request nothing would drain that
    ///     buffer, so it is written straight to the <see cref="IOutboxStore" />.
    /// </summary>
    /// <typeparam name="TNotification">The type of the notification.</typeparam>
    /// <param name="notification">The notification instance to publish.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the completion of the dispatch action.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Thrown if the outbox is enabled but the required <see cref="IOutbox" /> service is not registered in the DI container.
    /// </exception>
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        // The unit of work is only consulted in Transactional mode. With the outbox disabled (the default) this is the
        // hot path of every request's lifecycle notifications, so it must not pay for a DI lookup it never uses.
        var useOutbox = _outboxOptions.Mode switch
        {
            OutboxMode.Enabled => true,
            OutboxMode.Transactional => _provider.GetService<IUnitOfWork>() is IExplicitUnitOfWork { HasActiveTransaction: true },
            _ => false
        };

        if (!useOutbox) return _directDispatcher.Publish(notification, cancellationToken);

        var stableNameProvider = _provider.GetService<IStableNotificationNameProvider>();
        if (stableNameProvider is null)
            return _directDispatcher.Publish(notification, cancellationToken);

        // Durable storage requires a stable notification name. We treat this as the opt-in signal
        // for the outbox (keeps internal/framework notifications in-process and avoids fragile serialization).
        // Asked per provider, never cached statically: two hosts in one process may register different modules.
        if (!stableNameProvider.TryGetStableName(notification.GetType(), out _))
            return _directDispatcher.Publish(notification, cancellationToken);

        // Buffer only while a request is executing in this scope — that is what guarantees a flush. A publish from
        // outside a request (a controller, a hosted service) has no flush owner, so it goes straight to the store
        // rather than into a buffer that is dropped when the scope ends.
        var buffering = _provider.GetService<OutboxBufferingState>();
        if (buffering is { IsActive: false })
            return RequestOutboxScope.StoreAsync(_provider, [notification], cancellationToken);

        var outbox = _provider.GetService<IOutbox>();
        if (outbox is null)
            throw new InvalidOperationException(
                "Outbox mode is active, but the IOutbox service is not registered. " +
                "Ensure AddCqrsGenerated(...) ran during startup, and resolve ICqrsDispatcher from a DI scope " +
                "(not the root provider) so the scoped IOutbox is available.");
        outbox.Add(notification);
        return Task.CompletedTask;
    }
}