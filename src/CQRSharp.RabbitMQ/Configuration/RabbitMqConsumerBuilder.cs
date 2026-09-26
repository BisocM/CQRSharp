namespace CQRSharp.RabbitMQ;

/// <summary>
///     Configures one consumer: the queue it consumes (a durable quorum queue by default, with a dead-letter queue), what the
///     queue is bound to, and how its messages are taken in. Every message is taken into the outbox for the local handlers and
///     acknowledged only once stored.
/// </summary>
public sealed class RabbitMqConsumerBuilder
{
    internal RabbitMqConsumerBuilder(string queue) => Definition = new RabbitMqConsumerDefinition(queue);

    internal RabbitMqConsumerDefinition Definition { get; }

    /// <summary>
    ///     Binds the queue to <typeparamref name="TNotification" />'s stable name (<c>[NotificationName]</c>) on
    ///     <paramref name="exchange" />, or on the default exchange.
    /// </summary>
    /// <param name="exchange">The exchange to bind on, or <see langword="null" /> for <see cref="RabbitMqTransportOptions.DefaultExchange" />.</param>
    /// <typeparam name="TNotification">The notification to receive.</typeparam>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqConsumerBuilder Bind<TNotification>(string? exchange = null) where TNotification : INotification
    {
        Definition.Bindings.Add(new RabbitMqBindingDefinition(typeof(TNotification), null, exchange));
        return this;
    }

    /// <summary>
    ///     Binds the queue to <paramref name="routingPattern" /> on <paramref name="exchange" />, or on the default exchange:
    ///     a notification name, or a topic pattern over names (<c>orders.*</c>, <c>orders.#</c>).
    /// </summary>
    /// <param name="routingPattern">The binding key.</param>
    /// <param name="exchange">The exchange to bind on, or <see langword="null" /> for <see cref="RabbitMqTransportOptions.DefaultExchange" />.</param>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqConsumerBuilder Bind(string routingPattern, string? exchange = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routingPattern);
        Definition.Bindings.Add(new RabbitMqBindingDefinition(null, routingPattern, exchange));
        return this;
    }

    /// <summary>
    ///     How many unacknowledged messages the broker hands this consumer at once (1 to 65535). Defaults to 32.
    /// </summary>
    /// <param name="count">The prefetch count.</param>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqConsumerBuilder Prefetch(ushort count)
    {
        Definition.Prefetch = count;
        return this;
    }

    /// <summary>
    ///     How many messages are taken in at once (1 to 64). Messages that share a partition key always go through the same
    ///     lane, one after another in the order they arrived, so their order holds; messages of different keys proceed side by
    ///     side. Defaults to 4.
    /// </summary>
    /// <param name="count">The number of lanes.</param>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqConsumerBuilder Lanes(int count)
    {
        Definition.Lanes = count;
        return this;
    }

    /// <summary>
    ///     Declares the queue with a single active consumer (<c>x-single-active-consumer</c>): of the application's instances,
    ///     one consumes it and the others stand by. What keeps messages of one partition key in order across instances.
    /// </summary>
    /// <param name="enabled">Whether the queue has a single active consumer.</param>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqConsumerBuilder SingleActiveConsumer(bool enabled = true)
    {
        Definition.SingleActiveConsumer = enabled;
        return this;
    }

    /// <summary>
    ///     Declares a classic queue instead of a quorum queue, for a development broker or an older setup. A classic queue has
    ///     no delivery limit.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqConsumerBuilder Classic()
    {
        Definition.Classic = true;
        return this;
    }

    /// <summary>
    ///     How many times the broker delivers a message before it dead-letters it (<c>x-delivery-limit</c>), which bounds a
    ///     message that keeps being returned to the queue. Defaults to 20; <see langword="null" /> leaves the broker's
    ///     default. Quorum queues only.
    /// </summary>
    /// <param name="limit">The delivery limit, at least 1, or <see langword="null" />.</param>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqConsumerBuilder DeliveryLimit(int? limit)
    {
        Definition.DeliveryLimit = limit;
        Definition.DeliveryLimitSet = true;
        return this;
    }

    /// <summary>
    ///     Whether the queue dead-letters, through <see cref="RabbitMqTransportOptions.DeadLetterExchange" />, into a queue of
    ///     its own (<c>&lt;queue&gt;.dead-letter</c>): what cannot be read, is unknown past its grace period, or exceeded the
    ///     delivery limit goes there. Defaults to <see langword="true" />; without it the broker drops such messages.
    /// </summary>
    /// <param name="enabled">Whether the queue has a dead-letter queue.</param>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqConsumerBuilder DeadLetterQueue(bool enabled = true)
    {
        Definition.DeadLetterQueue = enabled;
        return this;
    }

    /// <summary>
    ///     The longest a message is held unacknowledged while it cannot be taken in (the outbox store is unreachable, or the
    ///     notification is not known yet) before it is returned to the queue. Must stay below the broker's
    ///     <c>consumer_timeout</c> (30 minutes by default), or the broker closes the channel. Defaults to 5 minutes.
    /// </summary>
    /// <param name="duration">The longest hold, above zero and below 30 minutes.</param>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqConsumerBuilder MaxHold(TimeSpan duration)
    {
        Definition.MaxHold = duration;
        return this;
    }
}
