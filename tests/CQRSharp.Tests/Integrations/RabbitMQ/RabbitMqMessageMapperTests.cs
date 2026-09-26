using System.Text;
using CQRSharp.RabbitMQ;
using CQRSharp.Transports;
using FluentAssertions;
using RabbitMQ.Client;

namespace CQRSharp.Tests.Integrations.RabbitMQ;

/// <summary>
///     The wire format, both ways, without a broker: what a published notification carries, and what a consumer reads back
///     from what arrives, including from a producer outside CQRSharp.
/// </summary>
public sealed class RabbitMqMessageMapperTests
{
    private static readonly DateTime CreatedAt = new(2026, 9, 20, 12, 0, 0, 123, DateTimeKind.Utc);

    [Fact(DisplayName = "RabbitMQ wire format: a notification is published persistent, with its id, name, time, key and trace context")]
    public void The_published_properties()
    {
        var notificationId = Guid.NewGuid();
        var message = new OutboundNotification(Guid.NewGuid(), notificationId, "orders.placed", [1], CreatedAt, "order-1",
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", "vendor=value", 2);

        var properties = RabbitMqMessageMapper.ToProperties(message, "application/json", "billing");

        properties.MessageId.Should().Be(message.MessageId.ToString("N"));
        properties.Type.Should().Be("orders.placed");
        properties.ContentType.Should().Be("application/json");
        properties.DeliveryMode.Should().Be(DeliveryModes.Persistent);
        properties.AppId.Should().Be("billing");
        properties.Timestamp.UnixTime.Should().Be(new DateTimeOffset(CreatedAt).ToUnixTimeSeconds());
        properties.Headers.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["cqrsharp-created-at"] = "2026-09-20T12:00:00.1230000Z",
            ["cqrsharp-notification-id"] = notificationId.ToString("D"),
            ["cqrsharp-partition-key"] = "order-1",
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            ["tracestate"] = "vendor=value"
        });
        RabbitMqMessageMapper.SentAt(properties).Should().Be(CreatedAt, "the header keeps the ticks the timestamp property drops");
    }

    [Fact(DisplayName = "RabbitMQ wire format: optional headers are left out when the notification has no key, id or trace")]
    public void Optional_headers_are_left_out()
    {
        var properties = RabbitMqMessageMapper.ToProperties(
            new OutboundNotification(Guid.NewGuid(), null, "orders.placed", [1], CreatedAt, null, null, "", 0), "application/json", "billing");

        properties.Headers.Should().ContainKey("cqrsharp-created-at").And.HaveCount(1);
    }

    [Theory(DisplayName = "RabbitMQ wire format: the time a message was sent is read from its header, as bytes or text, with or without an offset")]
    [InlineData("2026-09-20T12:00:00.1230000Z")]
    [InlineData("2026-09-20T14:00:00.1230000+02:00")]
    [InlineData("2026-09-20T12:00:00.1230000")]
    public void The_sent_time_is_read_from_the_header(string header)
    {
        var asText = new BasicProperties { Headers = new Dictionary<string, object?> { ["cqrsharp-created-at"] = header } };
        var asBytes = new BasicProperties { Headers = new Dictionary<string, object?> { ["cqrsharp-created-at"] = Encoding.UTF8.GetBytes(header) } };

        RabbitMqMessageMapper.SentAt(asText).Should().Be(CreatedAt);
        RabbitMqMessageMapper.SentAt(asBytes).Should().Be(CreatedAt);
    }

    [Fact(DisplayName = "RabbitMQ wire format: without a readable header the timestamp property is the sent time, and without either there is none")]
    public void The_timestamp_is_the_fallback()
    {
        var unreadable = new BasicProperties
        {
            Headers = new Dictionary<string, object?> { ["cqrsharp-created-at"] = "yesterday" },
            Timestamp = new AmqpTimestamp(new DateTimeOffset(CreatedAt).ToUnixTimeSeconds())
        };

        RabbitMqMessageMapper.SentAt(unreadable).Should().Be(CreatedAt.AddMilliseconds(-123));
        RabbitMqMessageMapper.SentAt(new BasicProperties()).Should().BeNull();
    }
}
