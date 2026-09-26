using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Domain.Events;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class PlaceOrderCommandHandler(ICqrsDispatcher dispatcher, TimeProvider timeProvider)
    : IResultCommandHandler<PlaceOrderCommand, Guid>
{
    public async Task<CommandResult<Guid>> Handle(PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        var orderId = Guid.NewGuid();

        await dispatcher.Publish(new OrderPlacedNotification
        {
            OrderId = orderId,
            PlacedAt = timeProvider.GetUtcNow(),
            Status = OrderStatus.Paid,
            ShipTo = command.ShipTo,
            Lines = command.Lines,
            Tags = command.Tags
        }, cancellationToken);

        return CommandResult<Guid>.FromSuccess(orderId);
    }
}
