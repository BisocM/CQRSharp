using Microsoft.Extensions.Logging;

namespace CQRSharp.RabbitMQ;

/// <summary>
///     The 8000 event-id block: what the RabbitMQ transport logs, under the category of the component that logs it. A
///     connection or consumer coming and going is Information, one that fails is a Warning (it is retried), and Error is
///     kept for what needs an operator: a topology that cannot be declared, a message rejected to the dead-letter queue.
///     What happens to one message is Debug, as in the outbox processor, which itself logs every send's outcome (5009,
///     5029 to 5031).
/// </summary>
internal static partial class RabbitMqLog
{
    [LoggerMessage(8000, LogLevel.Information, "RabbitMQ transport {TransportName}: the {Role} connection to {Endpoint} is open.")]
    public static partial void Connected(ILogger logger, string transportName, string role, string endpoint);

    [LoggerMessage(8001, LogLevel.Warning, "RabbitMQ transport {TransportName}: the {Role} connection was lost ({Reason}); reconnecting.")]
    public static partial void ConnectionLost(ILogger logger, string transportName, string role, string reason);

    [LoggerMessage(8002, LogLevel.Information, "RabbitMQ transport {TransportName}: the {Role} connection to {Endpoint} is open again.")]
    public static partial void Reconnected(ILogger logger, string transportName, string role, string endpoint);

    [LoggerMessage(8003, LogLevel.Warning, "RabbitMQ transport {TransportName}: opening the {Role} connection failed; retrying in {Delay}.")]
    public static partial void ConnectFailed(ILogger logger, Exception exception, string transportName, string role, TimeSpan delay);

    [LoggerMessage(8004, LogLevel.Warning, "RabbitMQ transport {TransportName}: the broker blocked the {Role} connection ({Reason}); sends are deferred until it is unblocked.")]
    public static partial void Blocked(ILogger logger, string transportName, string role, string reason);

    [LoggerMessage(8005, LogLevel.Information, "RabbitMQ transport {TransportName}: the broker unblocked the {Role} connection.")]
    public static partial void Unblocked(ILogger logger, string transportName, string role);

    [LoggerMessage(8010, LogLevel.Debug, "RabbitMQ transport {TransportName}: declared {Topology}.")]
    public static partial void TopologyDeclared(ILogger logger, string transportName, string topology);

    [LoggerMessage(8011, LogLevel.Error, "RabbitMQ transport {TransportName}: declaring {Topology} failed: {Detail} Declare it to match, or call AssumeExistingTopology() when the topology is managed outside the application.")]
    public static partial void TopologyFailed(ILogger logger, Exception? exception, string transportName, string topology, string detail);

    [LoggerMessage(8020, LogLevel.Information, "RabbitMQ transport {TransportName}: consuming queue {Queue} (prefetch {Prefetch}, {Lanes} lane(s)).")]
    public static partial void ConsumerStarted(ILogger logger, string transportName, string queue, ushort prefetch, int lanes);

    [LoggerMessage(8021, LogLevel.Information, "RabbitMQ transport {TransportName}: stopping the consumer of queue {Queue}.")]
    public static partial void ConsumerStopping(ILogger logger, string transportName, string queue);

    [LoggerMessage(8022, LogLevel.Warning, "RabbitMQ transport {TransportName}: the consumer of queue {Queue} stopped ({Reason}); consuming again in {Delay}.")]
    public static partial void ConsumerInterrupted(ILogger logger, string transportName, string queue, string reason, TimeSpan delay);

    [LoggerMessage(8023, LogLevel.Warning, "RabbitMQ transport {TransportName}: released {Count} unacknowledged message(s) of queue {Queue} at shutdown; the broker delivers them again.")]
    public static partial void DeliveriesReleased(ILogger logger, string transportName, string queue, int count);

    [LoggerMessage(8030, LogLevel.Error, "RabbitMQ transport {TransportName}: notification {NotificationType} (message {MessageId}) from queue {Queue} cannot be read ({Detail}); rejected to the dead-letter queue.")]
    public static partial void RejectedUnreadable(ILogger logger, string transportName, string notificationType, string? messageId, string queue, string? detail);

    [LoggerMessage(8031, LogLevel.Information, "RabbitMQ transport {TransportName}: notification {NotificationType} (message {MessageId}) from queue {Queue} is not known to this application; held for {RetryAfter} for an instance that knows it.")]
    public static partial void UnknownHeld(ILogger logger, string transportName, string notificationType, string? messageId, string queue, TimeSpan retryAfter);

    [LoggerMessage(8032, LogLevel.Error, "RabbitMQ transport {TransportName}: notification {NotificationType} (message {MessageId}) from queue {Queue} is not known to this application, and no instance took it in within the grace period; rejected to the dead-letter queue.")]
    public static partial void RejectedUnknown(ILogger logger, string transportName, string notificationType, string? messageId, string queue);

    [LoggerMessage(8033, LogLevel.Warning, "RabbitMQ transport {TransportName}: taking message {MessageId} from queue {Queue} in failed on attempt {Attempt}; holding it and trying again in {Delay}.")]
    public static partial void IntakeFailed(ILogger logger, Exception exception, string transportName, string? messageId, string queue, int attempt, TimeSpan delay);

    [LoggerMessage(8034, LogLevel.Error, "RabbitMQ transport {TransportName}: message {MessageId} from queue {Queue} has no type property, so it names no notification; rejected to the dead-letter queue.")]
    public static partial void RejectedUntyped(ILogger logger, string transportName, string? messageId, string queue);

    [LoggerMessage(8037, LogLevel.Error, "RabbitMQ transport {TransportName}: message {MessageId} from queue {Queue} could not be read off the channel; rejected to the dead-letter queue.")]
    public static partial void RejectedUnmappable(ILogger logger, Exception exception, string transportName, string? messageId, string queue);

    [LoggerMessage(8035, LogLevel.Warning, "RabbitMQ transport {TransportName}: message {MessageId} from queue {Queue} was held for {MaxHold} without being taken in; returned to the queue.")]
    public static partial void HoldLimitReached(ILogger logger, string transportName, string? messageId, string queue, TimeSpan maxHold);

    [LoggerMessage(8036, LogLevel.Error, "RabbitMQ transport {TransportName}: the consumer of queue {Queue} failed; consuming again in {Delay}.")]
    public static partial void ConsumerFailed(ILogger logger, Exception exception, string transportName, string queue, TimeSpan delay);

    [LoggerMessage(8038, LogLevel.Error, "RabbitMQ transport {TransportName}: taking message {MessageId} from queue {Queue} in failed unexpectedly; it is left unacknowledged, and the broker delivers it again once the channel closes.")]
    public static partial void DeliveryFailed(ILogger logger, Exception exception, string transportName, string? messageId, string queue);

    [LoggerMessage(8040, LogLevel.Debug, "RabbitMQ transport {TransportName}: message {MessageId} published to exchange {Exchange} with routing key {RoutingKey} was returned: no queue is bound to it.")]
    public static partial void Returned(ILogger logger, string transportName, Guid messageId, string exchange, string routingKey);

    [LoggerMessage(8041, LogLevel.Debug, "RabbitMQ transport {TransportName}: the broker did not confirm message {MessageId} published to exchange {Exchange}.")]
    public static partial void Nacked(ILogger logger, string transportName, Guid messageId, string exchange);
}
