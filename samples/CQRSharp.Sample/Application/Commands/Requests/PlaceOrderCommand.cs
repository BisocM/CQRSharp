using System.Data;
using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Domain.Events;

namespace CQRSharp.Sample.Application.Commands.Requests;

/// <summary>Places an order and returns its id. Transactional, so the event it publishes goes through the outbox.</summary>
public sealed class PlaceOrderCommand : ResultCommandBase<Guid, SampleRequestContext>, ITransactionalCommand
{
    public required ShippingAddress ShipTo { get; init; }
    public required IReadOnlyList<OrderLine> Lines { get; init; }
    public string[] Tags { get; init; } = [];

    public IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
}
