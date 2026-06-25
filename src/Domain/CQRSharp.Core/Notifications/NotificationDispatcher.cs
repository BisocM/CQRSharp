using System.Collections.Concurrent;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Interfaces.Transactions;
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
    private static readonly ConcurrentDictionary<Type, bool> HasStableNameCache = new();

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
    ///     Publishes a notification.
    ///     Depending on the configured <see cref="OutboxMode" />, the notification will either be
    ///     dispatched immediately to its handlers or added to a scoped <see cref="IOutbox" />
    ///     for deferred processing.
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
        var uow = _provider.GetService<IUnitOfWork>();
        var explicitUow = uow as IExplicitUnitOfWork;
        var isInTransaction = explicitUow?.HasActiveTransaction ?? false;

        var useOutbox = _outboxOptions.Mode switch
        {
            OutboxMode.Enabled => true,
            OutboxMode.Disabled => false,
            OutboxMode.Transactional => isInTransaction,
            _ => false
        };

        if (!useOutbox) return _directDispatcher.Publish(notification, cancellationToken);

        var stableNameProvider = _provider.GetService<IStableNotificationNameProvider>();
        if (stableNameProvider is null)
            return _directDispatcher.Publish(notification, cancellationToken);

        // Durable storage requires a stable notification name. We treat this as the opt-in signal
        // for the outbox (keeps internal/framework notifications in-process and avoids fragile serialization).
        var notificationType = notification.GetType();
        if (!HasStableNameCache.TryGetValue(notificationType, out var hasStableName))
        {
            hasStableName = stableNameProvider.TryGetStableName(notificationType, out _);
            HasStableNameCache.TryAdd(notificationType, hasStableName);
        }

        if (!hasStableName)
            return _directDispatcher.Publish(notification, cancellationToken);

        var outbox = _provider.GetService<IOutbox>();
        if (outbox is null)
            throw new InvalidOperationException(
                "Outbox mode is active, but the IOutbox service is not registered. " +
                "Ensure you have called services.AddCqrs() (or otherwise registered a scoped IOutbox).");
        outbox.Add(notification);
        return Task.CompletedTask;
    }
}