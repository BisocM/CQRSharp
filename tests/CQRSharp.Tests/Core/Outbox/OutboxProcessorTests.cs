using System.Text.Json;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Durability tests for the internal <see cref="OutboxProcessor" /> background service. Each test drives a single
///     polling cycle by gating the test on the terminal store call the cycle is expected to make, then stopping the
///     service. The store and serializer are mocked with Moq, the subscription registry is a fake whose handler
///     invokers the test controls, and all three are resolved through a fake <see cref="IServiceScopeFactory" />
///     (mirroring how <c>ProcessOutboxMessagesAsync</c> resolves them per scope). The store hands the batch out once,
///     so the poll that follows the cycle finds nothing; the processor reads a <see cref="FakeTimeProvider" /> that no
///     test advances, so the polling wait after that never ends on its own and every computed time is exact.
/// </summary>
public sealed class OutboxProcessorTests
{
    private const string HandlerName = "Tests.OutboxProcessorTestHandler";
    private const string SiblingHandlerName = "Tests.OutboxProcessorSiblingHandler";

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(1);
    private static readonly TimeSpan LongestRetryDelay = new OutboxRetryOptions().MaxDelay;

    private readonly Mock<INotificationSerializer> _serializer = new(MockBehavior.Strict);
    private readonly Mock<IOutboxStore> _store = new(MockBehavior.Strict);
    private readonly FakeSubscriptionRegistry _subscriptions = new();
    private readonly Dictionary<Type, object> _handlers = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // A claimed message created `age` ago, with a generous lease: these tests run one cycle on a clock that does not
    // move, so the processor never needs to renew.
    private ClaimedOutboxMessage Message(int attemptCount = 0, string type = "outbox.proc.test", string handler = HandlerName, TimeSpan age = default)
    {
        var id = Guid.NewGuid();
        var message = new OutboxMessage(
            id,
            type,
            handler,
            "{}"u8.ToArray(),
            Now - age,
            OutboxMessageStatus.InProgress,
            null,
            null,
            attemptCount);
        return new ClaimedOutboxMessage(message, new OutboxClaim(id, "test-claim", Now.AddMinutes(5)));
    }

    // Handed out once, as a real store would: the processor claims again right after a poll that found work, and by
    // then the batch's messages are no longer due.
    private void Claims(params ClaimedOutboxMessage[] batch)
    {
        var pending = new Queue<IReadOnlyList<ClaimedOutboxMessage>>([batch]);
        _store.Setup(s => s.ClaimPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => pending.TryDequeue(out var next) ? next : []);
    }

    private OutboxProcessor CreateProcessor(int maxAttempts = 3)
    {
        var options = Options.Create(new OutboxProcessorOptions
        {
            PollingInterval = PollingInterval,
            BatchSize = 100,
            MaxAttempts = maxAttempts,
            UnknownRecipientGracePeriod = GracePeriod
        });

        var scopeFactory = new OutboxScopeFactory(_store.Object, _serializer.Object, _subscriptions, _handlers);
        var publisher = new NotificationPublisher(
            scopeFactory.CreateScope().ServiceProvider, [], _subscriptions, new NotificationOptions(), new OutboxOptions(), metrics: null);
        return new OutboxProcessor(NullLogger<OutboxProcessor>.Instance, scopeFactory, options, publisher, _time);
    }

    // Subscribes the test's handler (or its sibling) to the test notification, delivering through handle.
    private void Subscribe(string handlerName, Func<IServiceProvider?, INotification, CancellationToken, Task> handle)
    {
        if (handlerName == HandlerName)
        {
            _handlers[typeof(OutboxProcessorTestHandler)] = new OutboxProcessorTestHandler(handle);
            _subscriptions.Add(NotificationSubscription.For<OutboxProcessorTestHandler, OutboxProcessorTestNotification>(handlerName));
        }
        else
        {
            _handlers[typeof(OutboxProcessorSiblingHandler)] = new OutboxProcessorSiblingHandler(handle);
            _subscriptions.Add(NotificationSubscription.For<OutboxProcessorSiblingHandler, OutboxProcessorTestNotification>(handlerName));
        }
    }

    /// <summary>
    ///     Starts the processor, awaits the gate (signalled from the terminal store call under test), then stops the
    ///     service. No wall-clock guard: how long the cycle takes depends on the machine's load, and a cycle that never
    ///     reaches its terminal call is caught by the test run's hang detection.
    /// </summary>
    private static async Task RunOneCycleAsync(OutboxProcessor processor, Task gate)
    {
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await gate.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Outbox: successful delivery invokes the addressed handler once and marks the message processed")]
    public async Task SuccessfulDispatch_MarksAsProcessed()
    {
        // Arrange
        var message = Message();
        INotification notification = new OutboxProcessorTestNotification();
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<INotification>();
        Subscribe(HandlerName, (_, n, _) =>
        {
            delivered.Add(n);
            return Task.CompletedTask;
        });

        Claims(message);
        _serializer.Setup(s => s.Deserialize(message.Message.NotificationType, message.Message.Payload))
            .Returns(notification);
        _store.Setup(s => s.MarkAsProcessedAsync(message.Claim, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => processed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(), processed.Task);

        // Assert
        delivered.Should().ContainSingle().Which.Should().BeSameAs(notification);
        _store.Verify(s => s.MarkAsProcessedAsync(message.Claim, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.IncrementAttemptAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsFailedAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Microsoft.Extensions.Hosting before 10.0 runs a BackgroundService's ExecuteAsync on the thread that starts the host
    // until its first await that does not complete at once; with the store answering at once, that reaches the first
    // delivery. From 10.0 the host starts it on the thread pool, where this holds trivially.
    [Fact(DisplayName = "Outbox: a delivery never posts back to the SynchronizationContext of the thread that starts the host")]
    public async Task Delivery_never_posts_to_the_starting_threads_context()
    {
        var message = Message();
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Subscribe(HandlerName, (_, _, _) => Task.CompletedTask);
        Claims(message);
        _serializer.Setup(s => s.Deserialize(message.Message.NotificationType, message.Message.Payload))
            .Returns(new OutboxProcessorTestNotification());
        _store.Setup(s => s.MarkAsProcessedAsync(message.Claim, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => processed.TrySetResult());
        var processor = CreateProcessor();
        var context = new PostCountingSynchronizationContext();

        Task starting;
        using (context.Install())
            starting = processor.StartAsync(CancellationToken.None);

        try
        {
            await starting;
            await processed.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        context.Posts.Should().Be(0);
    }

    [Fact(DisplayName = "Outbox: a delivered message whose outcome cannot be recorded is not counted as a failed attempt")]
    public async Task UnrecordedDelivery_IsNotAFailedAttempt()
    {
        // Arrange - the handler succeeds, then recording it fails (a store timeout, a lost connection).
        var message = Message(attemptCount: 2);
        INotification notification = new OutboxProcessorTestNotification();
        var markAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = 0;
        Subscribe(HandlerName, (_, _, _) =>
        {
            delivered++;
            return Task.CompletedTask;
        });

        Claims(message);
        _serializer.Setup(s => s.Deserialize(message.Message.NotificationType, message.Message.Payload))
            .Returns(notification);
        _store.Setup(s => s.MarkAsProcessedAsync(message.Claim, It.IsAny<CancellationToken>()))
            .Callback(() => markAttempted.TrySetResult())
            .ThrowsAsync(new TimeoutException("store timed out"));

        // Act
        await RunOneCycleAsync(CreateProcessor(maxAttempts: 3), markAttempted.Task);

        // Assert - the handler's work stands: no attempt is recorded and, on its last allowed attempt, nothing is
        // dead-lettered; the lease runs out and the message is delivered again.
        delivered.Should().Be(1);
        _store.Verify(s => s.IncrementAttemptAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsFailedAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "Outbox: a failing handler retries its own message and never makes its sibling's message run again")]
    public async Task SiblingHandlerFailure_DoesNotTouchTheHealthyHandlersMessage()
    {
        // Arrange - one notification, two handlers, two messages: the first handler throws, the second succeeds.
        var toFailing = Message(handler: HandlerName);
        var toHealthy = Message(handler: SiblingHandlerName);
        INotification notification = new OutboxProcessorTestNotification();
        var healthyCalls = 0;
        var failingCalls = 0;
        var healthyProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Subscribe(HandlerName, (_, _, _) =>
        {
            failingCalls++;
            throw new InvalidOperationException("sibling boom");
        });
        Subscribe(SiblingHandlerName, (_, _, _) =>
        {
            healthyCalls++;
            return Task.CompletedTask;
        });

        Claims(toFailing, toHealthy);
        _serializer.Setup(s => s.Deserialize("outbox.proc.test", It.IsAny<byte[]>()))
            .Returns(notification);
        _store.Setup(s => s.IncrementAttemptAsync(toFailing.Claim, It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _store.Setup(s => s.MarkAsProcessedAsync(toHealthy.Claim, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => healthyProcessed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(3), healthyProcessed.Task);

        // Assert - each message carries its own outcome: the failing handler's message is retried, the healthy
        // handler's message is processed, and the healthy handler ran exactly once.
        failingCalls.Should().Be(1);
        healthyCalls.Should().Be(1);
        _store.Verify(s => s.IncrementAttemptAsync(toFailing.Claim, It.Is<string?>(e => e != null && e.Contains("sibling boom")), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsProcessedAsync(toHealthy.Claim, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsProcessedAsync(toFailing.Claim, It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.IncrementAttemptAsync(toHealthy.Claim, It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "Outbox: handler throwing below the limit records a retry attempt (no dead-letter)")]
    public async Task HandlerThrows_BelowLimit_IncrementsAttempt_DoesNotDeadLetter()
    {
        // Arrange - first attempt: IncrementAttemptAsync returns 1, which is below MaxAttempts (3).
        var message = Message();
        INotification notification = new OutboxProcessorTestNotification();
        var handlerError = new InvalidOperationException("handler boom");
        var incremented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Subscribe(HandlerName, (_, _, _) => Task.FromException(handlerError));

        Claims(message);
        _serializer.Setup(s => s.Deserialize(message.Message.NotificationType, message.Message.Payload))
            .Returns(notification);
        _store.Setup(s => s.IncrementAttemptAsync(message.Claim, It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1)
            .Callback(() => incremented.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(3), incremented.Task);

        // Assert
        _store.Verify(s => s.IncrementAttemptAsync(message.Claim, It.Is<string?>(e => e != null && e.Contains("handler boom")), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _store.Verify(s => s.MarkAsFailedAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsProcessedAsync(It.IsAny<OutboxClaim>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "Outbox: a handler failing its last allowed attempt (MaxAttempts counts the first one) dead-letters the message")]
    public async Task HandlerThrows_AtLimit_MarksAsFailed()
    {
        // Arrange - two attempts already recorded, so this failure is the third (== MaxAttempts) -> dead-letter.
        // The processor dead-letters with ONE claim-checked call: recording the attempt first would return the message
        // to pending and end the claim, and the follow-up MarkAsFailed under it would be rejected.
        var message = Message(2);
        INotification notification = new OutboxProcessorTestNotification();
        var handlerError = new InvalidOperationException("terminal boom");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Subscribe(HandlerName, (_, _, _) => Task.FromException(handlerError));

        Claims(message);
        _serializer.Setup(s => s.Deserialize(message.Message.NotificationType, message.Message.Payload))
            .Returns(notification);
        _store.Setup(s => s.MarkAsFailedAsync(message.Claim, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => failed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(3), failed.Task);

        // Assert
        _store.Verify(s => s.IncrementAttemptAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsFailedAsync(message.Claim, It.Is<string?>(e => e != null && e.Contains("terminal boom")), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsProcessedAsync(It.IsAny<OutboxClaim>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory(DisplayName = "Outbox: a notification this instance does not know is deferred for another instance, without an attempt, never past the grace period")]
    [InlineData(0, 5)] // a fresh message waits one polling interval
    [InlineData(600, 300)] // an older one waits as long as it is old, up to the longest retry delay
    [InlineData(3570, 30)] // and never past the end of the grace period
    public async Task UnknownNotificationType_WithinGracePeriod_IsDeferred(int ageSeconds, int expectedDelaySeconds)
    {
        // Arrange - the serializer does not know the name: a newer instance of a mixed-version fleet may.
        var message = Message(type: "outbox.proc.unknown", age: TimeSpan.FromSeconds(ageSeconds));
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = false;
        Subscribe(HandlerName, (_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Claims(message);
        _serializer.Setup(s => s.Deserialize(message.Message.NotificationType, message.Message.Payload))
            .Returns((INotification?)null);
        _store.Setup(s => s.DeferAsync(message.Claim, It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => deferred.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(), deferred.Task);

        // Assert
        _store.Verify(s => s.DeferAsync(message.Claim, Now.AddSeconds(expectedDelaySeconds),
            It.Is<string?>(e => e != null && e.Contains(message.Message.NotificationType)), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsFailedAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.IncrementAttemptAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        invoked.Should().BeFalse();
    }

    [Fact(DisplayName = "Outbox: a notification no instance delivered within the grace period is dead-lettered, without retry")]
    public async Task UnknownNotificationType_PastGracePeriod_IsDeadLettered()
    {
        // Arrange - nothing has delivered the message for longer than the grace period: its name is unknown fleet-wide.
        var message = Message(type: "outbox.proc.unknown", age: GracePeriod + TimeSpan.FromSeconds(1));
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = false;
        Subscribe(HandlerName, (_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Claims(message);
        _serializer.Setup(s => s.Deserialize(message.Message.NotificationType, message.Message.Payload))
            .Returns((INotification?)null);
        _store.Setup(s => s.MarkAsFailedAsync(message.Claim, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => failed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(), failed.Task);

        // Assert
        _store.Verify(s => s.MarkAsFailedAsync(message.Claim,
            It.Is<string?>(e => e != null && e.Contains(message.Message.NotificationType) && e.Contains("grace period")), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.DeferAsync(It.IsAny<OutboxClaim>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.IncrementAttemptAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsProcessedAsync(It.IsAny<OutboxClaim>(), It.IsAny<CancellationToken>()), Times.Never);
        invoked.Should().BeFalse("the handler must never be touched for a message that cannot be deserialized");
    }

    [Fact(DisplayName = "Outbox: a message for a handler this instance does not have is deferred for an instance that has it, without an attempt")]
    public async Task UnknownHandlerName_WithinGracePeriod_IsDeferred()
    {
        // Arrange - the notification deserializes, but the handler was added by a newer version still rolling out.
        var message = Message(handler: "Tests.HandlerOnlyNewerInstancesHave", age: TimeSpan.FromMinutes(10));
        INotification notification = new OutboxProcessorTestNotification();
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = false;
        Subscribe(HandlerName, (_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Claims(message);
        _serializer.Setup(s => s.Deserialize(message.Message.NotificationType, message.Message.Payload))
            .Returns(notification);
        _store.Setup(s => s.DeferAsync(message.Claim, It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => deferred.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(), deferred.Task);

        // Assert - ten minutes old: deferred by the longest retry delay; no attempt spent, nothing dead-lettered.
        _store.Verify(s => s.DeferAsync(message.Claim, Now + LongestRetryDelay,
            It.Is<string?>(e => e != null && e.Contains("Tests.HandlerOnlyNewerInstancesHave")), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsFailedAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.IncrementAttemptAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        invoked.Should().BeFalse("the surviving handler is not the one the message is addressed to");
    }

    [Fact(DisplayName = "Outbox: a message addressed to a handler name nothing carried within the grace period is dead-lettered, without retry")]
    public async Task UnknownHandlerName_PastGracePeriod_IsDeadLettered()
    {
        // Arrange - the notification deserializes fine, but its handler was renamed or removed after the message was stored.
        var message = Message(handler: "Tests.HandlerThatWasRenamed", age: GracePeriod);
        INotification notification = new OutboxProcessorTestNotification();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = false;
        Subscribe(HandlerName, (_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Claims(message);
        _serializer.Setup(s => s.Deserialize(message.Message.NotificationType, message.Message.Payload))
            .Returns(notification);
        _store.Setup(s => s.MarkAsFailedAsync(message.Claim, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => failed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(), failed.Task);

        // Assert - no retry budget is spent; the dead letter names the missing handler and the fix.
        _store.Verify(s => s.MarkAsFailedAsync(message.Claim,
                It.Is<string?>(e => e != null && e.Contains("Tests.HandlerThatWasRenamed") && e.Contains("NotificationHandlerName")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _store.Verify(s => s.DeferAsync(It.IsAny<OutboxClaim>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.IncrementAttemptAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsProcessedAsync(It.IsAny<OutboxClaim>(), It.IsAny<CancellationToken>()), Times.Never);
        invoked.Should().BeFalse("the surviving handler is not the one the message is addressed to");
    }

    [Fact(DisplayName = "Outbox: a deferral the store fails to record does not abort the rest of the batch")]
    public async Task DeferFailure_DoesNotAbortBatch()
    {
        var unknown = Message(type: "outbox.proc.unknown");
        var healthy = Message(type: "outbox.proc.ok");
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Subscribe(HandlerName, (_, _, _) => Task.CompletedTask);

        Claims(unknown, healthy);
        _serializer.Setup(s => s.Deserialize(unknown.Message.NotificationType, unknown.Message.Payload))
            .Returns((INotification?)null);
        _store.Setup(s => s.DeferAsync(unknown.Claim, It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store is down"));
        _serializer.Setup(s => s.Deserialize(healthy.Message.NotificationType, healthy.Message.Payload))
            .Returns(new OutboxProcessorTestNotification());
        _store.Setup(s => s.MarkAsProcessedAsync(healthy.Claim, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => processed.TrySetResult());

        await RunOneCycleAsync(CreateProcessor(), processed.Task);

        _store.Verify(s => s.DeferAsync(unknown.Claim, It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsFailedAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsProcessedAsync(healthy.Claim, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "Outbox: corrupt payload (JsonException) dead-letters immediately with the real cause, no retry")]
    public async Task CorruptPayload_MarksAsFailed_WithRealCause_NoRetry()
    {
        // Arrange - a known type whose payload is corrupt: deserialize throws JsonException. This is deterministic,
        // so it must be dead-lettered immediately (NOT retried) and the recorded error must carry the real cause.
        var message = Message(type: "outbox.proc.corrupt");
        var jsonError = new JsonException("Unexpected token while reading outbox payload.");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = false;
        Subscribe(HandlerName, (_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Claims(message);
        _serializer.Setup(s => s.Deserialize(message.Message.NotificationType, message.Message.Payload))
            .Throws(jsonError);
        _store.Setup(s => s.MarkAsFailedAsync(message.Claim, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => failed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(), failed.Task);

        // Assert - the JsonException path records the exception's own text (jsonEx.ToString()), not the generic
        // "Failed to deserialize" message used for the unknown-type (null) path.
        _store.Verify(
            s => s.MarkAsFailedAsync(message.Claim, It.Is<string?>(e => e != null && e.Contains("Unexpected token while reading outbox payload.")), It.IsAny<CancellationToken>()),
            Times.Once);
        _store.Verify(s => s.IncrementAttemptAsync(It.IsAny<OutboxClaim>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsProcessedAsync(It.IsAny<OutboxClaim>(), It.IsAny<CancellationToken>()), Times.Never);
        invoked.Should().BeFalse();
    }

    [Fact(DisplayName = "Outbox: a store failure on the failure path does not abort the rest of the batch")]
    public async Task StoreFailureOnFailurePath_DoesNotAbortBatch()
    {
        // Arrange - two messages share one batch:
        //   #1 is corrupt (JsonException) and its MarkAsFailedAsync THROWS (the store itself is the failing dependency).
        //   #2 is a healthy message that must still be delivered and processed despite #1's store failure.
        var corrupt = Message(type: "outbox.proc.corrupt");
        var healthy = Message(type: "outbox.proc.ok");
        INotification notification = new OutboxProcessorTestNotification();
        var secondProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Subscribe(HandlerName, (_, _, _) => Task.CompletedTask);

        Claims(corrupt, healthy);

        _serializer.Setup(s => s.Deserialize(corrupt.Message.NotificationType, corrupt.Message.Payload))
            .Throws(new JsonException("corrupt"));
        _store.Setup(s => s.MarkAsFailedAsync(corrupt.Claim, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store is down")); // store failure on the failure path

        _serializer.Setup(s => s.Deserialize(healthy.Message.NotificationType, healthy.Message.Payload))
            .Returns(notification);
        _store.Setup(s => s.MarkAsProcessedAsync(healthy.Claim, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => secondProcessed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(), secondProcessed.Task);

        // Assert - the store failure on message #1 was swallowed and message #2 still completed successfully.
        _store.Verify(s => s.MarkAsFailedAsync(corrupt.Claim, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsProcessedAsync(healthy.Claim, It.IsAny<CancellationToken>()), Times.Once);
    }

    // A minimal notification used only as the deserializer's return value; its concrete shape is irrelevant here.
    private sealed record OutboxProcessorTestNotification : INotification;

    // The handlers the subscriptions name, one type per handler name (a subscription resolves its handler by type); each
    // runs what the test subscribed.
    private sealed class OutboxProcessorTestHandler(Func<IServiceProvider?, INotification, CancellationToken, Task> handle)
        : INotificationHandler<OutboxProcessorTestNotification>
    {
        public Task Handle(OutboxProcessorTestNotification notification, CancellationToken cancellationToken) => handle(null, notification, cancellationToken);
    }

    private sealed class OutboxProcessorSiblingHandler(Func<IServiceProvider?, INotification, CancellationToken, Task> handle)
        : INotificationHandler<OutboxProcessorTestNotification>
    {
        public Task Handle(OutboxProcessorTestNotification notification, CancellationToken cancellationToken) => handle(null, notification, cancellationToken);
    }

    /// <summary>
    ///     A minimal <see cref="IServiceScopeFactory" /> that resolves the three outbox collaborators the processor
    ///     pulls from each scope (<see cref="IOutboxStore" />, <see cref="INotificationSerializer" />,
    ///     <see cref="INotificationSubscriptionRegistry" />), the subscribed handlers, no notification behaviors, and null
    ///     for anything else.
    /// </summary>
    private sealed class OutboxScopeFactory(
        IOutboxStore store,
        INotificationSerializer serializer,
        INotificationSubscriptionRegistry subscriptions,
        IReadOnlyDictionary<Type, object> handlers) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new Provider(store, serializer, subscriptions, handlers));

        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;

            public void Dispose()
            {
            }
        }

        private sealed class Provider(
            IOutboxStore store,
            INotificationSerializer serializer,
            INotificationSubscriptionRegistry subscriptions,
            IReadOnlyDictionary<Type, object> handlers) : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IOutboxStore)) return store;
                if (serviceType == typeof(INotificationSerializer)) return serializer;
                if (serviceType == typeof(INotificationSubscriptionRegistry)) return subscriptions;
                if (serviceType == typeof(IEnumerable<INotificationPipelineBehavior<OutboxProcessorTestNotification>>))
                    return Array.Empty<INotificationPipelineBehavior<OutboxProcessorTestNotification>>();
                return handlers.GetValueOrDefault(serviceType);
            }
        }
    }
}
