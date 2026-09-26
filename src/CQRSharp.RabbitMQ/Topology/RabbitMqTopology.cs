using RabbitMQ.Client;

namespace CQRSharp.RabbitMQ;

/// <summary>
///     Declares what the transport uses, idempotently: declaring an exchange or queue that exists with the same settings
///     changes nothing, and one that exists with other settings fails with <c>PRECONDITION_FAILED</c> rather than being
///     changed. Each side declares what it uses: the publisher its exchanges; a consumer its exchanges, its queue, its
///     dead-letter exchange and queue, and its bindings.
/// </summary>
internal static class RabbitMqTopology
{
    /// <summary>The routing keys a consumer's queue is bound with: a notification type's stable name, or a pattern as given.</summary>
    public sealed record Binding(string Exchange, string RoutingKey);

    /// <summary>Declares <paramref name="exchanges" /> as durable topic exchanges.</summary>
    public static async Task DeclareExchangesAsync(IChannel channel, IEnumerable<string> exchanges, CancellationToken cancellationToken)
    {
        foreach (var exchange in exchanges)
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    ///     Declares a consumer's topology: the exchanges its bindings use, the dead-letter exchange and queue when it has one,
    ///     the queue itself (quorum unless classic, with its delivery limit, single active consumer and dead-lettering), and its
    ///     bindings.
    /// </summary>
    public static async Task DeclareConsumerAsync(
        IChannel channel,
        RabbitMqConsumerDefinition consumer,
        RabbitMqTransportOptions options,
        IReadOnlyList<Binding> bindings,
        CancellationToken cancellationToken)
    {
        await DeclareExchangesAsync(channel, bindings.Select(b => b.Exchange).Distinct(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);

        var queueType = consumer.Classic ? "classic" : "quorum";
        var arguments = new Dictionary<string, object?> { ["x-queue-type"] = queueType };
        if (!consumer.Classic && consumer.DeliveryLimit is { } limit)
            arguments["x-delivery-limit"] = limit;
        if (consumer.SingleActiveConsumer)
            arguments["x-single-active-consumer"] = true;

        if (consumer.DeadLetterQueue)
        {
            await channel.ExchangeDeclareAsync(options.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await channel.QueueDeclareAsync(consumer.DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = queueType }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await channel.QueueBindAsync(consumer.DeadLetterQueueName, options.DeadLetterExchange, consumer.Queue, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            arguments["x-dead-letter-exchange"] = options.DeadLetterExchange;
            arguments["x-dead-letter-routing-key"] = consumer.Queue;
        }

        await channel.QueueDeclareAsync(consumer.Queue, durable: true, exclusive: false, autoDelete: false, arguments: arguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var binding in bindings)
            await channel.QueueBindAsync(consumer.Queue, binding.Exchange, binding.RoutingKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
    }
}
