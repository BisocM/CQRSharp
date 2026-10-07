namespace CQRSharp.RabbitMQ;

/// <summary>
///     Configures one RabbitMQ transport: its name, what it publishes (which notifications leave the process) and what it
///     consumes (which queues bring notifications in). A published notification still reaches its local handlers; a
///     notification received through a queue reaches the local handlers only, and is never published back out.
/// </summary>
public sealed class RabbitMqTransportBuilder
{
    private readonly List<Action<RabbitMqTransportOptions>> _configure = [];
    private readonly List<string> _queues = [];

    internal RabbitMqTransportBuilder()
    {
    }

    /// <summary>The queues the transport consumes, in call order: one hosted consumer each.</summary>
    internal IReadOnlyList<string> ConsumedQueues => _queues.Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The transport's name.</summary>
    internal string TransportName { get; private set; } = RabbitMqTransportOptions.DefaultTransportName;

    /// <summary>
    ///     Names the transport, which its outbox messages are addressed to: <c>rabbitmq</c> unless set. Give a second transport
    ///     (a second broker) a name of its own. Must not change while messages addressed to it may be stored, and must not be
    ///     the name of a notification handler.
    /// </summary>
    /// <param name="transportName">The name, at most 256 characters.</param>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqTransportBuilder Name(string transportName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportName);
        if (transportName.Length > 256)
            throw new ArgumentException("A transport name is at most 256 characters: it is stored as an outbox message's handler name.", nameof(transportName));
        TransportName = transportName;
        return this;
    }

    /// <summary>Adjusts the transport's <see cref="RabbitMqTransportOptions" />.</summary>
    /// <param name="configure">The adjustment.</param>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqTransportBuilder Configure(Action<RabbitMqTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configure.Add(configure);
        return this;
    }

    /// <summary>
    ///     Publishes every <typeparamref name="TNotification" /> to RabbitMQ through the outbox, as well as delivering it to
    ///     its local handlers. It must be durable: its stable name (<c>[NotificationName]</c>) is its routing key and the
    ///     receiver reads it back by it. Published again for the same notification, the last configuration wins.
    /// </summary>
    /// <param name="configure">Adjusts the exchange, the routing key, or whether it may go unrouted.</param>
    /// <typeparam name="TNotification">The notification type; publishing it as a base type or interface routes it too.</typeparam>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqTransportBuilder Publish<TNotification>(Action<RabbitMqPublication>? configure = null) where TNotification : INotification
    {
        var definition = Configured(configure);
        _configure.Add(options => options.PublicationsByType[typeof(TNotification)] = definition);
        return this;
    }

    /// <summary>
    ///     Publishes every notification stored under <paramref name="notificationName" /> to RabbitMQ through the outbox, as
    ///     well as delivering it to its local handlers: for a notification declared in an assembly this one does not name the
    ///     type of, or named by a custom serializer. Published again for the same name, the last configuration wins.
    /// </summary>
    /// <param name="notificationName">The notification's stable name.</param>
    /// <param name="configure">Adjusts the exchange, the routing key, or whether it may go unrouted.</param>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqTransportBuilder Publish(string notificationName, Action<RabbitMqPublication>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationName);
        var definition = Configured(configure);
        _configure.Add(options => options.PublicationsByName[notificationName] = definition);
        return this;
    }

    /// <summary>
    ///     Consumes <paramref name="queue" />: every notification that arrives there is taken into the outbox for the local
    ///     handlers, deduplicated by its message id, and acknowledged once stored. The queue's name is the application's
    ///     subscription: every instance consumes the same queue, and each message goes to one of them.
    /// </summary>
    /// <param name="queue">The queue's name.</param>
    /// <param name="configure">The queue's bindings and how it is consumed.</param>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqTransportBuilder Consume(string queue, Action<RabbitMqConsumerBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(configure);
        var consumer = new RabbitMqConsumerBuilder(queue);
        configure(consumer);
        _queues.Add(queue);
        _configure.Add(options => options.Consumers.Add(consumer.Definition));
        return this;
    }

    /// <summary>
    ///     Declares nothing: the exchanges, queues and bindings are managed outside the application, and a missing one shows up
    ///     as a rejected publish or a consumer that cannot start.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public RabbitMqTransportBuilder AssumeExistingTopology()
    {
        _configure.Add(options => options.DeclareTopology = false);
        return this;
    }

    /// <summary>Applies every call, in call order, to the transport's options.</summary>
    internal void Apply(RabbitMqTransportOptions options)
    {
        foreach (var configure in _configure)
            configure(options);
    }

    private static RabbitMqPublicationDefinition Configured(Action<RabbitMqPublication>? configure)
    {
        var publication = new RabbitMqPublication();
        configure?.Invoke(publication);
        return publication.ToDefinition();
    }
}
