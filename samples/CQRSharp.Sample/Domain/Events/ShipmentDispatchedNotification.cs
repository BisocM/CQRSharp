namespace CQRSharp.Sample.Domain.Events;

/// <summary>
///     Published when a shipment leaves the warehouse: an integration event. The sample's RabbitMQ scenario forwards it to
///     the broker from one application and takes it in from a queue in another; its stable name is its routing key and what
///     the receiver reads it back by, and its order id keeps one order's shipments in order.
/// </summary>
[NotificationName("sample.shipment.dispatched", PartitionBy = nameof(OrderId))]
public sealed record ShipmentDispatchedNotification(Guid ShipmentId, Guid OrderId, string Carrier) : INotification;
