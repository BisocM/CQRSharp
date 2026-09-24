using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Notifications.Handlers;

public sealed class OrderPlacedNotificationHandler(SampleDiagnostics diagnostics) : INotificationHandler<OrderPlacedNotification>
{
    public Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken)
    {
        diagnostics.RecordOrderPlaced(notification);
        return Task.CompletedTask;
    }
}
