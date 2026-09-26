using System.Diagnostics;
using System.Net.Sockets;
using CQRSharp.Transports;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace CQRSharp.RabbitMQ;

/// <summary>
///     The RabbitMQ transport's outbound side: sends a stored notification as a mandatory, persistent publish and reports it
///     sent only once the broker confirmed it, which it does once every queue it is routed to has it. An outage (no
///     connection, a blocked connection, a nack, a confirm that did not come in time) is Unavailable, never an attempt; a
///     publish no queue is bound to receive, or to an exchange that does not exist or may not be written, is Rejected; one
///     larger than the broker accepts is rejected permanently.
/// </summary>
internal sealed class RabbitMqNotificationTransport : INotificationTransport, IAsyncDisposable, IDisposable
{
    private const string MessagingSystem = "messaging.system";
    private const string MessagingDestination = "messaging.destination.name";
    private const string MessagingRoutingKey = "messaging.rabbitmq.destination.routing_key";
    private const string MessagingMessageId = "messaging.message.id";

    private readonly RabbitMqTransportRuntime _runtime;
    private readonly ILogger _logger;
    private readonly PublisherChannelPool _channels = new();

    public RabbitMqNotificationTransport(RabbitMqTransportRuntime runtime, ILogger logger)
    {
        _runtime = runtime;
        _logger = logger;
        var options = runtime.Options;
        Declaration = new NotificationTransportDeclaration
        {
            PublishedTypes = options.PublicationsByType.Keys.ToArray(),
            PublishedNames = options.PublicationsByName.Keys.ToArray(),
            ConsumedTypes = options.Consumers.SelectMany(c => c.Bindings).Where(b => b.NotificationType is not null)
                .Select(b => b.NotificationType!).Distinct().ToArray(),
            // A topic pattern stands for names no check can resolve; only an exact name is declared.
            ConsumedNames = options.Consumers.SelectMany(c => c.Bindings).Select(b => b.RoutingPattern)
                .Where(p => p is not null && !p.Contains('*') && !p.Contains('#')).Select(p => p!).Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    public string Name => _runtime.Name;

    public NotificationTransportDeclaration Declaration { get; }

    public bool Routes(string notificationName, Type notificationType) => _runtime.Publications.ContainsKey(notificationName);

    public async Task<TransportSendResult> SendAsync(OutboundNotification message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var options = _runtime.Options;
        if (!_runtime.Publications.TryGetValue(message.NotificationName, out var publication))
            return TransportSendResult.Rejected(
                $"RabbitMQ transport '{Name}' is not configured to publish '{message.NotificationName}' (any more). Configure its " +
                "publication again and requeue the dead letter, or purge it.", permanent: true);

        if (message.Payload.Length > options.MaxMessageSize)
            return TransportSendResult.Rejected(
                $"The payload has {message.Payload.Length} bytes, more than the {options.MaxMessageSize} the transport publishes " +
                "(RabbitMqTransportOptions.MaxMessageSize, which should match the broker's max_message_size).", permanent: true);

        var publisher = _runtime.Publisher;
        publisher.Start();
        if (publisher.Current is not { } connection)
            return TransportSendResult.Unavailable("The connection to RabbitMQ is not open.", publisher.RetryDelay);
        if (publisher.BlockedReason is { } blocked)
            return TransportSendResult.Unavailable($"RabbitMQ blocked the connection ({blocked}).", publisher.RetryDelay);

        Tag(publication, message);

        IChannel channel;
        try
        {
            channel = await _channels.RentAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsOutage(ex, connection))
        {
            return TransportSendResult.Unavailable($"Opening a channel failed: {ex.Message}", publisher.RetryDelay);
        }

        var reusable = false;
        using var confirmTimeout = new CancellationTokenSource(options.PublishTimeout, _runtime.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, confirmTimeout.Token);
        try
        {
            var properties = RabbitMqMessageMapper.ToProperties(message, options.ContentType, _runtime.AppId);
            await channel.BasicPublishAsync(publication.Exchange, publication.RoutingKey, mandatory: true, properties, message.Payload, linked.Token)
                .ConfigureAwait(false);
            reusable = true;
            return TransportSendResult.Sent;
        }
        catch (PublishException ex) when (ex.IsReturn)
        {
            reusable = true;
            RabbitMqLog.Returned(_logger, Name, message.MessageId, publication.Exchange, publication.RoutingKey);
            return publication.AllowUnroutable
                ? TransportSendResult.Sent
                : TransportSendResult.Rejected(
                    $"No queue is bound to exchange '{publication.Exchange}' with routing key '{publication.RoutingKey}', so the broker " +
                    "returned the message. Bind a queue to it (a consumer's Bind), or AllowUnroutable() the publication.");
        }
        catch (PublishException)
        {
            // The broker could not take it (a queue that rejects publishes when full, a quorum queue without its majority).
            reusable = true;
            RabbitMqLog.Nacked(_logger, Name, message.MessageId, publication.Exchange);
            return TransportSendResult.Unavailable("The broker did not take the message (it nacked the publish).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (confirmTimeout.IsCancellationRequested)
        {
            return TransportSendResult.Unavailable(
                $"The broker did not confirm the publish within {options.PublishTimeout}. It may still have taken it: the message is sent " +
                "again, with the same message id.");
        }
        catch (OperationInterruptedException ex) when (connection.IsOpen && RabbitMqTransportRuntime.IsRefusal(ex, out var detail))
        {
            // A channel-level error: the channel is closed, the connection stands.
            return ex.ShutdownReason?.ReplyCode == 406 && detail.Contains("message size", StringComparison.OrdinalIgnoreCase)
                ? TransportSendResult.Rejected($"The broker refused the message as too large: {detail}", permanent: true)
                : TransportSendResult.Rejected($"The broker refused the publish to exchange '{publication.Exchange}': {detail}");
        }
        catch (Exception ex) when (IsOutage(ex, connection))
        {
            return TransportSendResult.Unavailable($"The connection to RabbitMQ failed during the publish: {ex.Message}", publisher.RetryDelay);
        }
        finally
        {
            if (reusable) await _channels.ReturnAsync(channel).ConfigureAwait(false);
            else await _channels.DiscardAsync(channel).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => _channels.DisposeAsync();

    public void Dispose() => _channels.Dispose();

    // The outbox dispatch span is current while the processor sends: it gets the messaging attributes of the publish.
    private void Tag(ResolvedPublication publication, OutboundNotification message)
    {
        if (Activity.Current is not { IsAllDataRequested: true } activity) return;

        activity.SetTag(MessagingSystem, "rabbitmq");
        activity.SetTag(MessagingDestination, publication.Exchange);
        activity.SetTag(MessagingRoutingKey, publication.RoutingKey);
        activity.SetTag(MessagingMessageId, message.MessageId.ToString("N"));
    }

    // The connection going away, as opposed to the broker refusing the publish: nothing about the message is wrong. Any
    // failure once the connection is closed is one; a closed channel on an open connection is the broker refusing it.
    private static bool IsOutage(Exception exception, IConnection connection)
        => !connection.IsOpen || exception is BrokerUnreachableException or IOException or SocketException or ObjectDisposedException;
}
