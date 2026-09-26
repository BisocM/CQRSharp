namespace CQRSharp.Sample.AspNetCore.Orders;

/// <summary>
///     The body a client posts. The endpoint copies it onto <see cref="PlaceOrder" /> rather than binding the command
///     itself, so a client can set neither the command's context nor its idempotency key through the body.
/// </summary>
public sealed record PlaceOrderBody(string Sku, int Quantity);

/// <summary>
///     Places an order and returns its id. Idempotent: the endpoint builds the key from the caller and the Idempotency-Key
///     header, and a retry under it is answered with the original id, replayed from the idempotency store.
/// </summary>
public sealed class PlaceOrder : ResultCommandBase<Guid>, IIdempotentRequest
{
    public required string Sku { get; init; }
    public required int Quantity { get; init; }
    public required string IdempotencyKey { get; init; }
}

public sealed class PlaceOrderValidator : IRequestValidator<PlaceOrder>
{
    public Task<ValidationFailure[]> ValidateAsync(PlaceOrder request, CancellationToken cancellationToken)
    {
        var failures = new List<ValidationFailure>();
        if (string.IsNullOrWhiteSpace(request.Sku))
            failures.Add(new ValidationFailure("SKU_REQUIRED", "A SKU is required.", nameof(PlaceOrder.Sku)));
        if (request.Quantity <= 0)
            failures.Add(new ValidationFailure("QUANTITY_RANGE", "Quantity must be positive.", nameof(PlaceOrder.Quantity)));

        return Task.FromResult(failures.ToArray());
    }
}

public sealed class PlaceOrderHandler(OrderBook orders) : IResultCommandHandler<PlaceOrder, Guid>
{
    public Task<CommandResult<Guid>> Handle(PlaceOrder command, CancellationToken cancellationToken)
    {
        var order = new OrderDto(Guid.NewGuid(), command.Sku, command.Quantity, OrderStatus.Placed);
        orders.Add(order);
        return Task.FromResult(CommandResult<Guid>.FromSuccess(order.Id));
    }
}
