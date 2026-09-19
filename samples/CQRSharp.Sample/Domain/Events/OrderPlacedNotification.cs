using CQRSharp.Abstractions.Attributes.Notifications;
using CQRSharp.Abstractions.Interfaces.Notifications;

namespace CQRSharp.Sample.Domain.Events;

// Exercises the source-generated, AOT-safe outbox serializer over a non-trivial shape: nested objects, a
// collection of nested objects, an array, scalars, and an enum. Defining it is enough for the NativeAOT publish
// to compile — and thereby validate — every generated WriteObj/ReadObj/WriteColl/ReadColl helper, because the
// generated INotificationSerializer that references them is DI-rooted by the outbox. The hand-rolled emitter uses
// only Utf8JsonReader/Utf8JsonWriter (no reflection, no JsonSerializer), so it stays trim/AOT-clean.
[NotificationName("sample.order.placed")]
public sealed class OrderPlacedNotification : INotification
{
    public Guid OrderId { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public OrderStatus Status { get; set; }
    public ShippingAddress ShipTo { get; set; } = new();
    public IReadOnlyList<OrderLine> Lines { get; set; } = new List<OrderLine>();
    public string[] Tags { get; set; } = Array.Empty<string>();
}

public enum OrderStatus
{
    Pending,
    Paid,
    Shipped
}

public sealed class ShippingAddress
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
}

public sealed class OrderLine
{
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
