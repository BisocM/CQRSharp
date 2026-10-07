namespace CQRSharp.RabbitMQ;

/// <summary>
///     How one notification is published to RabbitMQ: by default to <see cref="RabbitMqTransportOptions.DefaultExchange" />,
///     with the notification's stable name (<c>[NotificationName]</c>) as the routing key, and as a failed attempt when no
///     queue is bound to receive it.
/// </summary>
public sealed class RabbitMqPublication
{
    internal RabbitMqPublication()
    {
    }

    internal string? Exchange { get; private set; }

    internal string? RoutingKey { get; private set; }

    internal bool Unroutable { get; private set; }

    /// <summary>Publishes to <paramref name="exchange" />, a durable topic exchange the transport declares, instead of the default exchange.</summary>
    /// <param name="exchange">The exchange's name.</param>
    /// <returns>The same publication, for chaining.</returns>
    public RabbitMqPublication ToExchange(string exchange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        Exchange = exchange;
        return this;
    }

    /// <summary>Publishes with <paramref name="routingKey" /> instead of the notification's name.</summary>
    /// <param name="routingKey">The routing key.</param>
    /// <returns>The same publication, for chaining.</returns>
    public RabbitMqPublication WithRoutingKey(string routingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        RoutingKey = routingKey;
        return this;
    }

    /// <summary>
    ///     Counts a publish that no queue is bound to receive as sent, and so drops it, instead of as a failed attempt that is
    ///     retried and finally dead-lettered in the outbox. For a notification that is broadcast to whoever listens, and may
    ///     go unheard.
    /// </summary>
    /// <returns>The same publication, for chaining.</returns>
    public RabbitMqPublication AllowUnroutable()
    {
        Unroutable = true;
        return this;
    }

    internal RabbitMqPublicationDefinition ToDefinition() => new(Exchange, RoutingKey, Unroutable);
}
