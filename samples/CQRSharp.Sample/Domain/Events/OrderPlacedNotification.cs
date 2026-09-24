namespace CQRSharp.Sample.Domain.Events;

/// <summary>
///     Published when an order is placed. Its shape (nested objects, a list of them, an array, an enum, scalars, all set
///     through property setters) is what the generated outbox serializer has to write and read back; the self-test sends
///     one through the outbox and compares what the handler receives with what was published.
/// </summary>
[NotificationName("sample.order.placed")]
public sealed class OrderPlacedNotification : INotification
{
    public Guid OrderId { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public OrderStatus Status { get; set; }
    public ShippingAddress ShipTo { get; set; } = new();
    public IReadOnlyList<OrderLine> Lines { get; set; } = [];
    public string[] Tags { get; set; } = [];
}

public enum OrderStatus
{
    Pending,
    Paid,
    Shipped
}

public sealed record ShippingAddress
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
}

public sealed record OrderLine
{
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
