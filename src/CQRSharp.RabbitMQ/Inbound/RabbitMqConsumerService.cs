using CQRSharp.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace CQRSharp.RabbitMQ;

/// <summary>What a consumer is doing, for the health check.</summary>
internal enum RabbitMqConsumerStatus
{
    /// <summary>Waiting for its connection, or setting up its channel.</summary>
    Starting,

    /// <summary>Consuming its queue.</summary>
    Consuming,

    /// <summary>Its channel or subscription was lost; it consumes again after a back-off.</summary>
    Interrupted,

    /// <summary>The broker refused its topology or its subscription (a declaration that differs, a queue that does not exist).</summary>
    Refused,

    /// <summary>The host stopped it.</summary>
    Stopped
}

/// <summary>
///     Consumes one queue: every message is copied off the channel, queued on its lane, taken into the outbox through the
///     intake (in a DI scope of its own) and only then acknowledged. A message that cannot be read, or that nobody knows
///     past the grace period, is rejected to the queue's dead-letter queue; one that cannot be taken in yet (the store is
///     unreachable, the notification is unknown but may be known soon) is held on its lane and tried again, for at most
///     <see cref="RabbitMqConsumerBuilder.MaxHold" />, then returned to the queue. A lost channel or a subscription the broker
///     cancelled is consumed again after a back-off. At shutdown it stops taking deliveries, lets the intakes in progress
///     finish, and leaves the rest unacknowledged for the broker to deliver again.
/// </summary>
internal sealed class RabbitMqConsumerService : BackgroundService
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);

    private readonly RabbitMqTransportRuntime _runtime;
    private readonly RabbitMqConsumerDefinition _definition;
    private readonly IServiceScopeFactory _scopes;
    private readonly OutboxRetryOptions _retry;
    private readonly ILogger _logger;
    private readonly string _source;

    // Cancelled when the host begins to stop: nothing new starts, and held messages are let go.
    private readonly CancellationTokenSource _stopping = new();

    // Cancelled when the host's stop budget runs out: the intakes still writing give up too.
    private readonly CancellationTokenSource _abandon = new();

    private readonly SemaphoreSlim _settle = new(1, 1);
    private readonly object _gate = new();
    private IChannel? _channel;
    private string? _consumerTag;
    private IntakeLanes? _lanes;
    private int _released;

    public RabbitMqConsumerService(
        RabbitMqTransportRuntime runtime,
        RabbitMqConsumerDefinition definition,
        IServiceScopeFactory scopes,
        OutboxProcessorOptions processorOptions,
        ILogger logger)
    {
        _runtime = runtime;
        _definition = definition;
        _scopes = scopes;
        _retry = processorOptions.Retry;
        _logger = logger;
        _source = $"{runtime.Name}:{definition.Queue}";
    }

    /// <summary>The queue it consumes.</summary>
    public string Queue => _definition.Queue;

    /// <summary>What it is doing.</summary>
    public RabbitMqConsumerStatus Status { get; private set; } = RabbitMqConsumerStatus.Starting;

    /// <summary>Why it is not consuming, when it is not.</summary>
    public string? StatusDetail { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var link = _runtime.Consumer ?? throw new InvalidOperationException($"RabbitMQ transport '{_runtime.Name}' has no consumer connection.");
        var delay = FirstRetryDelay;
        while (!stoppingToken.IsCancellationRequested && !_stopping.IsCancellationRequested)
        {
            string reason;
            try
            {
                Status = RabbitMqConsumerStatus.Starting;
                var connection = await link.WaitAsync(stoppingToken).ConfigureAwait(false);
                var interruption = await ConsumeAsync(connection, () => delay = FirstRetryDelay, stoppingToken).ConfigureAwait(false);
                if (interruption is null) return;

                reason = interruption;
                Status = RabbitMqConsumerStatus.Interrupted;
                StatusDetail = reason;
                RabbitMqLog.ConsumerInterrupted(_logger, _runtime.Name, Queue, reason, delay);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _stopping.IsCancellationRequested)
            {
                return;
            }
            catch (TopologyRefusedException refused)
            {
                // Waiting does not fix a declaration that differs from what exists, but an operator can, and the consumer then
                // starts without a restart; it retries at the longest back-off meanwhile, reporting unhealthy.
                Status = RabbitMqConsumerStatus.Refused;
                StatusDetail = refused.Message;
                RabbitMqLog.TopologyFailed(_logger, refused.InnerException, _runtime.Name, $"the topology of queue '{Queue}'", refused.Message);
                delay = _runtime.Options.ReconnectMaxDelay;
            }
            catch (Exception ex)
            {
                Status = RabbitMqConsumerStatus.Interrupted;
                StatusDetail = ex.Message;
                RabbitMqLog.ConsumerFailed(_logger, ex, _runtime.Name, Queue, delay);
            }

            try
            {
                await Task.Delay(delay, _runtime.TimeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = delay * 2 < _runtime.Options.ReconnectMaxDelay ? delay * 2 : _runtime.Options.ReconnectMaxDelay;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        RabbitMqLog.ConsumerStopping(_logger, _runtime.Name, Queue);
        await _stopping.CancelAsync().ConfigureAwait(false);

        IChannel? channel;
        string? consumerTag;
        IntakeLanes? lanes;
        lock (_gate)
        {
            channel = _channel;
            consumerTag = _consumerTag;
            lanes = _lanes;
        }

        // No new deliveries; the ones already on the lanes are let go (held or not started) or finish (being written).
        if (channel is { IsOpen: true } && consumerTag is not null)
            try
            {
                await channel.BasicCancelAsync(consumerTag, noWait: false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationInterruptedException or OperationCanceledException or IOException)
            {
                // The channel is going away anyway; what it held is requeued when it closes.
            }

        if (lanes is not null)
        {
            lanes.Complete();
            using var giveUp = cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _abandon);
            try
            {
                await lanes.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The stop budget ran out: the intakes still running were told to give up.
            }
        }

        var released = Interlocked.Exchange(ref _released, 0);
        if (released > 0)
            RabbitMqLog.DeliveriesReleased(_logger, _runtime.Name, Queue, released);

        // Ends the consume loop, which closes the channel: the broker requeues what is still unacknowledged.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        Status = RabbitMqConsumerStatus.Stopped;
    }

    public override void Dispose()
    {
        _stopping.Dispose();
        _abandon.Dispose();
        _settle.Dispose();
        base.Dispose();
    }

    // Consumes on a channel of its own until the channel or the subscription is lost (returns why) or the host stops
    // (returns null). The channel is closed on the way out.
    private async Task<string?> ConsumeAsync(IConnection connection, Action consuming, CancellationToken stoppingToken)
    {
        var channel = await connection.CreateChannelAsync(new CreateChannelOptions(false, false, consumerDispatchConcurrency: 1), stoppingToken)
            .ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            var interrupted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.ChannelShutdownAsync += (_, e) =>
            {
                if (e.Initiator != ShutdownInitiator.Application) interrupted.TrySetResult($"the channel closed ({RabbitMqLink.Describe(e)})");
                return Task.CompletedTask;
            };

            try
            {
                if (_runtime.Options.DeclareTopology)
                {
                    if (!_runtime.TryResolveBindings(_definition, out var bindings, out var failure))
                        throw new TopologyRefusedException(failure, null);
                    await RabbitMqTopology.DeclareConsumerAsync(channel, _definition, _runtime.Options, bindings, stoppingToken).ConfigureAwait(false);
                }

                await channel.BasicQosAsync(0, _definition.Prefetch, false, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationInterruptedException ex) when (connection.IsOpen && RabbitMqTransportRuntime.IsRefusal(ex, out var detail))
            {
                throw new TopologyRefusedException(detail, ex);
            }

            var lanes = new IntakeLanes(_definition.Lanes, _definition.Prefetch, ProcessAsync);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => OnReceivedAsync(channel, lanes, delivery);
            consumer.UnregisteredAsync += (_, _) =>
            {
                // The broker cancelled the subscription (the queue was deleted); the host's own cancel is not an interruption.
                if (!_stopping.IsCancellationRequested) interrupted.TrySetResult("the broker cancelled the subscription");
                return Task.CompletedTask;
            };

            string consumerTag;
            try
            {
                consumerTag = await channel.BasicConsumeAsync(Queue, autoAck: false, consumerTag: string.Empty, noLocal: false, exclusive: false,
                    arguments: null, consumer, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationInterruptedException ex) when (connection.IsOpen && RabbitMqTransportRuntime.IsRefusal(ex, out var detail))
            {
                lanes.Complete();
                throw new TopologyRefusedException(detail, ex);
            }

            lock (_gate)
            {
                _channel = channel;
                _consumerTag = consumerTag;
                _lanes = lanes;
            }

            Status = RabbitMqConsumerStatus.Consuming;
            StatusDetail = null;
            consuming();
            RabbitMqLog.ConsumerStarted(_logger, _runtime.Name, Queue, _definition.Prefetch, _definition.Lanes);

            try
            {
                return await interrupted.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                // What the lanes still hold belongs to this channel; it is requeued when the channel closes, and a lane that
                // reaches it skips it.
                lanes.Complete();
                lock (_gate)
                {
                    _channel = null;
                    _consumerTag = null;
                    _lanes = null;
                }
            }
        }
    }

    // The consumer's dispatch loop runs one delivery at a time: it only copies the message (its body is valid only during
    // this call) and queues it on its lane. A message that cannot even be copied off the channel (a header no mapping
    // expects) is rejected to the dead-letter queue: left unacknowledged it would hold a prefetch slot until the channel
    // closed, and return to do it again.
    private async Task OnReceivedAsync(IChannel channel, IntakeLanes lanes, BasicDeliverEventArgs delivery)
    {
        ReceivedDelivery received;
        try
        {
            received = Copy(channel, delivery);
        }
        catch (Exception ex)
        {
            RabbitMqLog.RejectedUnmappable(_logger, ex, _runtime.Name, delivery.BasicProperties.MessageId, Queue);
            await SettleAsync(channel, c => c.BasicRejectAsync(delivery.DeliveryTag, requeue: false)).ConfigureAwait(false);
            return;
        }

        // A delivery that arrives while the consumer is being cancelled finds the lanes closed: it stays unacknowledged, and
        // the broker delivers it again once the channel closes.
        if (!lanes.TryPost(received) && _stopping.IsCancellationRequested)
            Interlocked.Increment(ref _released);
    }

    private ReceivedDelivery Copy(IChannel channel, BasicDeliverEventArgs delivery)
    {
        var properties = delivery.BasicProperties;
        var type = properties.Type;
        var message = new InboundNotification(
            _runtime.Name,
            _source,
            properties.MessageId,
            type ?? string.Empty,
            delivery.Body.ToArray(),
            RabbitMqMessageMapper.SentAt(properties),
            RabbitMqMessageMapper.Header(properties, RabbitMqMessageMapper.TraceParentHeader),
            RabbitMqMessageMapper.Header(properties, RabbitMqMessageMapper.TraceStateHeader));
        return new ReceivedDelivery(
            channel,
            delivery.DeliveryTag,
            message,
            RabbitMqMessageMapper.Header(properties, RabbitMqMessageMapper.PartitionKeyHeader),
            !string.IsNullOrEmpty(type),
            _runtime.TimeProvider.GetUtcNow().UtcDateTime);
    }

    // One delivery, from its lane: taken in, held and tried again while it cannot be yet, then settled on its channel.
    // Never throws: a lane must go on with the next delivery.
    private async Task ProcessAsync(ReceivedDelivery delivery)
    {
        var message = delivery.Message;
        var attempt = 0;
        try
        {
            while (true)
            {
                // Stopping, or its channel is gone: it is left unacknowledged, and the broker delivers it again.
                if (_stopping.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _released);
                    return;
                }

                if (!delivery.Channel.IsOpen) return;

                TimeSpan hold;
                try
                {
                    var result = await AcceptAsync(message).ConfigureAwait(false);
                    switch (result.Outcome)
                    {
                        case IntakeOutcome.Stored or IntakeOutcome.Duplicate or IntakeOutcome.NoSubscribers:
                            await SettleAsync(delivery.Channel, c => c.BasicAckAsync(delivery.DeliveryTag, multiple: false)).ConfigureAwait(false);
                            return;

                        case IntakeOutcome.UnknownNotification when result.RetryAfter is { } retryAfter:
                            RabbitMqLog.UnknownHeld(_logger, _runtime.Name, message.NotificationName, message.MessageId, Queue, retryAfter);
                            hold = retryAfter;
                            break;

                        case IntakeOutcome.UnknownNotification:
                            RabbitMqLog.RejectedUnknown(_logger, _runtime.Name, message.NotificationName, message.MessageId, Queue);
                            await SettleAsync(delivery.Channel, c => c.BasicRejectAsync(delivery.DeliveryTag, requeue: false)).ConfigureAwait(false);
                            return;

                        default:
                            if (delivery.HasType)
                                RabbitMqLog.RejectedUnreadable(_logger, _runtime.Name, message.NotificationName, message.MessageId, Queue, result.Detail);
                            else
                                RabbitMqLog.RejectedUntyped(_logger, _runtime.Name, message.MessageId, Queue);
                            await SettleAsync(delivery.Channel, c => c.BasicRejectAsync(delivery.DeliveryTag, requeue: false)).ConfigureAwait(false);
                            return;
                    }
                }
                catch (OperationCanceledException) when (_abandon.IsCancellationRequested)
                {
                    // The host's stop budget ran out during the intake: left unacknowledged, and delivered again.
                    Interlocked.Increment(ref _released);
                    return;
                }
                catch (Exception ex)
                {
                    attempt++;
                    hold = RetryDelay(attempt);
                    RabbitMqLog.IntakeFailed(_logger, ex, _runtime.Name, message.MessageId, Queue, attempt, hold);
                }

                // Held on its lane, unacknowledged, which keeps the later messages of its key behind it; never longer than the
                // hold limit, below the broker's consumer timeout, after which it is returned to the queue.
                var held = _runtime.TimeProvider.GetUtcNow().UtcDateTime - delivery.ReceivedAt;
                var remaining = _definition.MaxHold - held;
                if (remaining <= TimeSpan.Zero)
                {
                    RabbitMqLog.HoldLimitReached(_logger, _runtime.Name, message.MessageId, Queue, _definition.MaxHold);
                    await SettleAsync(delivery.Channel, c => c.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true)).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await Task.Delay(hold < remaining ? hold : remaining, _runtime.TimeProvider, _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _released);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Nothing above is meant to throw; if something does, the delivery stays unacknowledged and the broker delivers it
            // again once the channel closes, which beats losing the lane.
            RabbitMqLog.DeliveryFailed(_logger, ex, _runtime.Name, message.MessageId, Queue);
        }
    }

    // In a scope of its own, like any outbox delivery; the token only gives up once the host's stop budget has run out, so
    // a stop never cancels a write half done.
    private async Task<IntakeResult> AcceptAsync(InboundNotification message)
    {
        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
            return await scope.ServiceProvider.GetRequiredService<INotificationIntake>().AcceptAsync(message, _abandon.Token).ConfigureAwait(false);
    }

    // Acknowledgements are written one at a time: the lanes settle side by side, on one channel.
    private async Task SettleAsync(IChannel channel, Func<IChannel, ValueTask> settle)
    {
        await _settle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (channel.IsOpen)
                await settle(channel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationInterruptedException or IOException or ObjectDisposedException)
        {
            // The channel closed under the settle: the broker delivers the message again, and the intake recognises it.
        }
        finally
        {
            _settle.Release();
        }
    }

    // The outbox's retry back-off, without jitter: base delay, grown by the multiplier per attempt, capped.
    private TimeSpan RetryDelay(int attempt)
    {
        var grown = _retry.BaseDelay.TotalMilliseconds * Math.Pow(_retry.BackoffMultiplier, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(grown, _retry.MaxDelay.TotalMilliseconds));
    }

    /// <summary>The broker refused the consumer's topology or subscription, or the application's configuration cannot express it.</summary>
    private sealed class TopologyRefusedException(string message, Exception? inner) : Exception(message, inner);
}
