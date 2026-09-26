using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace CQRSharp.Sample.AspNetCore.Orders;

public sealed record OrderDto(Guid Id, string Sku, int Quantity, OrderStatus Status);

[JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
public enum OrderStatus
{
    Placed,
    Cancelled
}

public enum CancelOutcome
{
    Cancelled,
    AlreadyCancelled,
    NotFound
}

/// <summary>The sample's order store: in memory, so it forgets everything when the process ends.</summary>
public sealed class OrderBook
{
    private readonly ConcurrentDictionary<Guid, OrderDto> _orders = new();

    public void Add(OrderDto order)
    {
        if (!_orders.TryAdd(order.Id, order))
            throw new InvalidOperationException($"Order {order.Id} already exists.");
    }

    public OrderDto? Find(Guid id) => _orders.GetValueOrDefault(id);

    public CancelOutcome Cancel(Guid id)
    {
        while (_orders.TryGetValue(id, out var order))
        {
            if (order.Status == OrderStatus.Cancelled) return CancelOutcome.AlreadyCancelled;
            if (_orders.TryUpdate(id, order with { Status = OrderStatus.Cancelled }, order)) return CancelOutcome.Cancelled;
        }

        return CancelOutcome.NotFound;
    }
}
