using CQRSharp.Sample.AspNetCore.Callers;

namespace CQRSharp.Sample.AspNetCore.Orders;

/// <summary>
///     Looks an order up. Its context is the <see cref="CallerContext" />, so lookups are rate limited per caller.
/// </summary>
public sealed class GetOrder(Guid orderId) : QueryBase<OrderDto?, CallerContext>
{
    public Guid OrderId { get; } = orderId;
}

public sealed class GetOrderHandler(OrderBook orders) : IQueryHandler<GetOrder, OrderDto?>
{
    public Task<OrderDto?> Handle(GetOrder query, CancellationToken cancellationToken)
        => Task.FromResult(orders.Find(query.OrderId));
}
