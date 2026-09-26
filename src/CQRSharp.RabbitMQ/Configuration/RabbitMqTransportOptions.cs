namespace CQRSharp.RabbitMQ;

/// <summary>
///     The settings of one RabbitMQ transport: named options, one set per transport, named after it (see
///     <see cref="RabbitMqTransportBuilder.Name" />). Validated when the host starts, in every environment.
/// </summary>
public sealed class RabbitMqTransportOptions
{
    /// <summary>The name a transport is given unless <see cref="RabbitMqTransportBuilder.Name" /> sets another.</summary>
    public const string DefaultTransportName = "rabbitmq";

    /// <summary>
    ///     The durable topic exchange notifications are published to unless a publication names another, with the
    ///     notification's name as the routing key. Defaults to <c>cqrsharp.notifications</c>.
    /// </summary>
    public string DefaultExchange { get; set; } = "cqrsharp.notifications";

    /// <summary>
    ///     The durable direct exchange a consumer's queue dead-letters to, bound to its dead-letter queue
    ///     (<c>&lt;queue&gt;.dead-letter</c>) by the queue's name. Defaults to <c>cqrsharp.dead-letter</c>.
    /// </summary>
    public string DeadLetterExchange { get; set; } = "cqrsharp.dead-letter";

    /// <summary>
    ///     Whether the transport declares the exchanges, queues and bindings it uses, which it does idempotently whenever it
    ///     connects. Defaults to <see langword="true" />; <see cref="RabbitMqTransportBuilder.AssumeExistingTopology" /> turns
    ///     it off for a topology managed elsewhere.
    /// </summary>
    public bool DeclareTopology { get; set; } = true;

    /// <summary>
    ///     How long a publish waits for the broker's confirm before the send is reported unavailable and retried (the broker
    ///     may still have taken it, so the retry may deliver it twice, with the same message id). Defaults to 30 seconds.
    /// </summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The largest payload, in bytes, the transport publishes; a larger one is rejected permanently and dead-lettered in
    ///     the outbox, since the broker would refuse it on every attempt. Keep it at or below the broker's
    ///     <c>max_message_size</c>. Defaults to 16 MiB, the broker's default.
    /// </summary>
    public int MaxMessageSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>The <c>app-id</c> of the published messages; the host's application name when not set.</summary>
    public string? AppId { get; set; }

    /// <summary>
    ///     The <c>content-type</c> of the published messages. Defaults to <c>application/json</c>, what the source-generated
    ///     serializer writes; set it when a custom notification serializer writes another format.
    /// </summary>
    public string ContentType { get; set; } = "application/json";

    /// <summary>
    ///     The prefix of the names the transport's connections show in the broker's management UI (followed by
    ///     <c>/publish</c> or <c>/consume</c>); <c>&lt;app-id&gt;/&lt;transport name&gt;</c> when not set.
    /// </summary>
    public string? ClientProvidedName { get; set; }

    /// <summary>
    ///     The longest wait between two attempts to open a connection, or to consume again after a consumer stopped: the
    ///     wait starts at one second and doubles up to this. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>What the builder's <c>Publish</c> verbs configured, by notification name.</summary>
    internal Dictionary<string, RabbitMqPublicationDefinition> PublicationsByName { get; } = new(StringComparer.Ordinal);

    /// <summary>What the builder's <c>Publish&lt;T&gt;</c> verbs configured, by notification type.</summary>
    internal Dictionary<Type, RabbitMqPublicationDefinition> PublicationsByType { get; } = new();

    /// <summary>What the builder's <c>Consume</c> verbs configured, in call order.</summary>
    internal List<RabbitMqConsumerDefinition> Consumers { get; } = [];
}

/// <summary>Where one notification is published: its exchange and routing key, and whether it may go unrouted.</summary>
internal sealed record RabbitMqPublicationDefinition(string? Exchange, string? RoutingKey, bool AllowUnroutable);

/// <summary>A queue binding of a consumer: by notification type or by routing pattern, to an exchange or the default one.</summary>
internal sealed record RabbitMqBindingDefinition(Type? NotificationType, string? RoutingPattern, string? Exchange);

/// <summary>One consumer: its queue, the bindings it declares, and how it consumes.</summary>
internal sealed class RabbitMqConsumerDefinition(string queue)
{
    public string Queue { get; } = queue;
    public List<RabbitMqBindingDefinition> Bindings { get; } = [];
    public ushort Prefetch { get; set; } = 32;
    public int Lanes { get; set; } = 4;
    public bool SingleActiveConsumer { get; set; }
    public bool Classic { get; set; }
    public int? DeliveryLimit { get; set; } = 20;
    public bool DeliveryLimitSet { get; set; }
    public bool DeadLetterQueue { get; set; } = true;
    public TimeSpan MaxHold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The queue that takes what this queue dead-letters.</summary>
    public string DeadLetterQueueName => Queue + ".dead-letter";
}
