using System.Data;

namespace CQRSharp.Sample.Application.Commands.Requests;

/// <summary>Dispatches a shipment in a transaction, whose commit stores the integration event it publishes.</summary>
public sealed class DispatchShipmentCommand(Guid shipmentId, Guid orderId, string carrier) : CommandBase, ITransactionalCommand
{
    public Guid ShipmentId { get; } = shipmentId;
    public Guid OrderId { get; } = orderId;
    public string Carrier { get; } = carrier;
    public IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
}
