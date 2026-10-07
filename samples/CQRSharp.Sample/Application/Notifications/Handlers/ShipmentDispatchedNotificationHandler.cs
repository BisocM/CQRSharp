using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Notifications.Handlers;

// Records the shipment in the application it ran in: the RabbitMQ scenario tells the publishing application's delivery
// from the one that took it in from the broker by whose diagnostics hold it.
public sealed class ShipmentDispatchedNotificationHandler(SampleDiagnostics diagnostics) : INotificationHandler<ShipmentDispatchedNotification>
{
    public Task Handle(ShipmentDispatchedNotification notification, CancellationToken cancellationToken)
    {
        diagnostics.RecordShipmentDispatched(notification);
        return Task.CompletedTask;
    }
}
