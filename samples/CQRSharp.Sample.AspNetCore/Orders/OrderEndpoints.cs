using CQRSharp.AspNetCore;
using CQRSharp.Sample.AspNetCore.Callers;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CQRSharp.Sample.AspNetCore.Orders;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/orders");

        // 201 Created with the id; a failure as ProblemDetails. GetIdempotencyKey() throws for a missing or malformed
        // header, which AddCqrsProblemDetails maps to 400. The idempotency stores' key space is global, so the key is
        // scoped by the caller: two callers who pick the same header value never see each other's order.
        orders.MapPost("/", async (PlaceOrderBody body, HttpContext http, ICqrsDispatcher dispatcher, CancellationToken ct) =>
        {
            var command = new PlaceOrder
            {
                Sku = body.Sku,
                Quantity = body.Quantity,
                IdempotencyKey = $"{CallerIdentity.Of(http)}:{http.GetIdempotencyKey()}"
            };
            var result = await dispatcher.Send(command, ct);
            return result.ToCreatedHttpResult(id => $"/orders/{id}");
        });

        orders.MapGet("/{id:guid}", async Task<Results<Ok<OrderDto>, NotFound>> (Guid id, ICqrsDispatcher dispatcher, CancellationToken ct) =>
            await dispatcher.Send(new GetOrder(id), ct) is { } order ? TypedResults.Ok(order) : TypedResults.NotFound());

        // 204 No Content; NotFound -> 404 and Conflict -> 409, as ProblemDetails.
        orders.MapDelete("/{id:guid}", async (Guid id, ICqrsDispatcher dispatcher, CancellationToken ct) =>
            (await dispatcher.Send(new CancelOrder(id), ct)).ToHttpResult());

        return app;
    }
}
