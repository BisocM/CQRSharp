namespace CQRSharp.Sample.AspNetCore.Orders;

/// <summary>Cancels an order. An unknown order and one already cancelled are expected outcomes, returned, not thrown.</summary>
public sealed class CancelOrder(Guid orderId) : CommandBase
{
    public Guid OrderId { get; } = orderId;
}

public sealed class CancelOrderHandler(OrderBook orders) : ICommandHandler<CancelOrder>
{
    public Task<CommandResult> Handle(CancelOrder command, CancellationToken cancellationToken)
        => Task.FromResult(orders.Cancel(command.OrderId) switch
        {
            CancelOutcome.Cancelled => CommandResult.FromSuccess(),
            CancelOutcome.AlreadyCancelled => CommandResult.Conflict("The order is already cancelled."),
            _ => CommandResult.NotFound("There is no order with that id.")
        });
}
