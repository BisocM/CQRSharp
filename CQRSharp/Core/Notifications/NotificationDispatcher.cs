using CQRSharp.Shared.Data.Interfaces.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Notifications;

/// <inheritdoc />
public sealed class NotificationDispatcher(IServiceScopeFactory scopeFactory) : INotificationDispatcher
{
    /// <inheritdoc />
    public async Task Publish<TNotification>(TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        using var scope = scopeFactory.CreateScope();
        var handlers = scope.ServiceProvider
            .GetServices<INotificationHandler<TNotification>>();
        var tasks    = handlers.Select(h => h.Handle(notification, cancellationToken));
        await Task.WhenAll(tasks);
    }
}