using System.Globalization;
using System.Text;
using CQRSharp.Transports;
using RabbitMQ.Client;

namespace CQRSharp.RabbitMQ;

/// <summary>
///     The wire format of a notification on RabbitMQ, both ways: the AMQP properties and headers a published notification
///     carries, and what the consumer reads back from them. What a producer or consumer outside CQRSharp exchanges with it.
/// </summary>
internal static class RabbitMqMessageMapper
{
    /// <summary>The id every message of one publish shares: the local deliveries' and each transport's.</summary>
    public const string NotificationIdHeader = "cqrsharp-notification-id";

    /// <summary>The notification's ordering key, when it has one.</summary>
    public const string PartitionKeyHeader = "cqrsharp-partition-key";

    /// <summary>When the notification was published (or fell due), to the tick: the <c>timestamp</c> property has whole seconds.</summary>
    public const string CreatedAtHeader = "cqrsharp-created-at";

    /// <summary>The W3C trace context headers, under the names RabbitMQ.Client's own propagation uses.</summary>
    public const string TraceParentHeader = "traceparent";

    /// <inheritdoc cref="TraceParentHeader" />
    public const string TraceStateHeader = "tracestate";

    /// <summary>The properties of a published notification: persistent, identified, typed by its name, and traced.</summary>
    public static BasicProperties ToProperties(OutboundNotification message, string contentType, string appId)
    {
        var createdAt = DateTime.SpecifyKind(message.CreatedAt, DateTimeKind.Utc);
        var headers = new Dictionary<string, object?>(5, StringComparer.Ordinal)
        {
            [CreatedAtHeader] = createdAt.ToString("O", CultureInfo.InvariantCulture)
        };
        if (message.NotificationId is { } notificationId)
            headers[NotificationIdHeader] = notificationId.ToString("D");
        if (message.PartitionKey is { } partitionKey)
            headers[PartitionKeyHeader] = partitionKey;
        if (message.TraceParent is { } traceParent)
            headers[TraceParentHeader] = traceParent;
        if (!string.IsNullOrEmpty(message.TraceState))
            headers[TraceStateHeader] = message.TraceState;

        return new BasicProperties
        {
            MessageId = message.MessageId.ToString("N"),
            Type = message.NotificationName,
            ContentType = contentType,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(new DateTimeOffset(createdAt).ToUnixTimeSeconds()),
            AppId = appId,
            Headers = headers
        };
    }

    /// <summary>A header as text: the client hands a string header back as bytes (an AMQP long string).</summary>
    public static string? Header(IReadOnlyBasicProperties properties, string name)
        => properties.Headers is { } headers && headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null
            }
            : null;

    /// <summary>
    ///     When the notification was sent: its creation header, else its <c>timestamp</c> property; <see langword="null" /> when
    ///     it carries neither, or a creation header that is not a round-trip date, for a producer that sets no time.
    /// </summary>
    public static DateTime? SentAt(IReadOnlyBasicProperties properties)
    {
        // A time with an offset is converted, one without is taken as UTC, as CQRSharp writes it.
        if (Header(properties, CreatedAtHeader) is { } text &&
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
            return createdAt.Kind switch
            {
                DateTimeKind.Utc => createdAt,
                DateTimeKind.Local => createdAt.ToUniversalTime(),
                _ => DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)
            };

        return properties.IsTimestampPresent() && properties.Timestamp.UnixTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime).UtcDateTime
            : null;
    }
}
