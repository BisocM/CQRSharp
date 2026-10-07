using System.Text;
using Microsoft.Extensions.Options;

namespace CQRSharp.RabbitMQ;

/// <summary>
///     Rejects, when the host starts, a RabbitMQ transport that cannot run: a transport without the outbox
///     (<c>CQRCONF013</c>, in every environment, since nothing would ever reach the broker), a name RabbitMQ refuses, a
///     setting out of range. Registered once however many transports are, and asked for each transport's named options.
/// </summary>
internal sealed class RabbitMqTransportOptionsValidator(IOptions<OutboxOptions> outbox) : IValidateOptions<RabbitMqTransportOptions>
{
    // What AMQP 0-9-1 allows for an exchange, queue or routing key: a short string, 255 bytes.
    private const int MaxNameBytes = 255;

    // The longest a CancellationTokenSource can wait for.
    private static readonly TimeSpan MaxTimer = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    // The broker's own consumer_timeout default; an unacknowledged message held longer closes the channel.
    private static readonly TimeSpan ConsumerTimeout = TimeSpan.FromMinutes(30);

    public ValidateOptionsResult Validate(string? name, RabbitMqTransportOptions options)
    {
        var failures = Failures(name ?? Options.DefaultName, options).ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private IEnumerable<string> Failures(string transport, RabbitMqTransportOptions options)
    {
        var prefix = $"RabbitMQ transport '{transport}':";

        if (outbox.Value.Mode == OutboxMode.Disabled)
            yield return $"{prefix} CQRSharp configuration error CQRCONF013: the outbox mode is 'Disabled'. A RabbitMQ transport " +
                         "forwards what the outbox stores and stores what it receives in the outbox, so nothing would ever reach " +
                         "the broker or the local handlers. Configure it inside UseOutbox(o => o.UseRabbitMq(...)), or turn the " +
                         "outbox on.";

        foreach (var failure in NameFailures(prefix, "DefaultExchange", options.DefaultExchange))
            yield return failure;
        foreach (var failure in NameFailures(prefix, "DeadLetterExchange", options.DeadLetterExchange))
            yield return failure;

        if (options.PublishTimeout <= TimeSpan.Zero || options.PublishTimeout > MaxTimer)
            yield return $"{prefix} PublishTimeout must be above zero and at most {MaxTimer}.";
        if (options.MaxMessageSize is < 1 or > 512 * 1024 * 1024)
            yield return $"{prefix} MaxMessageSize must be between 1 byte and 512 MiB, the largest message RabbitMQ accepts.";
        if (options.ReconnectMaxDelay < TimeSpan.FromSeconds(1) || options.ReconnectMaxDelay > MaxTimer)
            yield return $"{prefix} ReconnectMaxDelay must be at least one second (the first retry waits that long) and at most {MaxTimer}.";
        if (string.IsNullOrWhiteSpace(options.ContentType))
            yield return $"{prefix} ContentType must not be empty.";

        foreach (var (key, publication) in options.PublicationsByName.Select(p => (p.Key, p.Value))
                     .Concat(options.PublicationsByType.Select(p => (Key: p.Key.FullName ?? p.Key.Name, p.Value))))
        {
            if (publication.Exchange is { } exchange)
                foreach (var failure in NameFailures(prefix, $"the exchange of the publication of '{key}'", exchange))
                    yield return failure;
            if (publication.RoutingKey is { } routingKey)
                foreach (var failure in NameFailures(prefix, $"the routing key of the publication of '{key}'", routingKey))
                    yield return failure;
        }

        var queues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var consumer in options.Consumers)
        {
            var queue = consumer.Queue;
            foreach (var failure in NameFailures(prefix, "a consumer's queue", queue))
                yield return failure;
            if (!queues.Add(queue))
                yield return $"{prefix} the queue '{queue}' is consumed twice; configure it with one Consume call.";
            if (consumer.DeadLetterQueue && Encoding.UTF8.GetByteCount(consumer.DeadLetterQueueName) > MaxNameBytes)
                yield return $"{prefix} the dead-letter queue of '{queue}' ('{consumer.DeadLetterQueueName}') is longer than {MaxNameBytes} bytes; shorten the queue's name.";

            // The intake records a message under "<transport>:<queue>", in the 256 characters of an inbox record's name.
            if (transport.Length + 1 + queue.Length > 256)
                yield return $"{prefix} the transport's name and the queue '{queue}' are together longer than the 255 characters the inbox keeps of where a message came from.";

            if (consumer.Prefetch < 1)
                yield return $"{prefix} the prefetch of queue '{queue}' must be at least 1 (0 would let the broker send without limit).";
            if (consumer.Lanes is < 1 or > 64)
                yield return $"{prefix} the lanes of queue '{queue}' must be between 1 and 64.";
            if (consumer.MaxHold <= TimeSpan.Zero || consumer.MaxHold >= ConsumerTimeout)
                yield return $"{prefix} the MaxHold of queue '{queue}' must be above zero and below 30 minutes, the broker's default consumer_timeout.";
            if (consumer.DeliveryLimit is < 1)
                yield return $"{prefix} the delivery limit of queue '{queue}' must be at least 1 when set.";
            if (consumer.Classic && consumer.DeliveryLimitSet && consumer.DeliveryLimit is not null)
                yield return $"{prefix} queue '{queue}' is a classic queue, which has no delivery limit; remove DeliveryLimit(...) or use a quorum queue.";

            foreach (var binding in consumer.Bindings)
            {
                if (binding.Exchange is { } exchange)
                    foreach (var failure in NameFailures(prefix, $"a binding exchange of queue '{queue}'", exchange))
                        yield return failure;
                if (binding.RoutingPattern is { } pattern)
                    foreach (var failure in NameFailures(prefix, $"a binding of queue '{queue}'", pattern))
                        yield return failure;
            }
        }
    }

    private static IEnumerable<string> NameFailures(string prefix, string what, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield return $"{prefix} {what} must not be empty.";
        else if (Encoding.UTF8.GetByteCount(value) > MaxNameBytes)
            yield return $"{prefix} {what} ('{value}') is longer than the {MaxNameBytes} bytes RabbitMQ allows.";
    }
}
