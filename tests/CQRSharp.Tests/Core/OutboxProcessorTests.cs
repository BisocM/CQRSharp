using System.Text.Json;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Models.Outbox;
using CQRSharp.Core.Background.Outbox;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Durability tests for the internal <see cref="OutboxProcessor" /> background service. Each test drives a single
///     polling cycle by gating the test on the terminal store call the cycle is expected to make, then stopping the
///     service. The store, serializer and dispatcher are mocked with Moq and resolved through a fake
///     <see cref="IServiceScopeFactory" /> (mirroring how <c>ProcessOutboxMessagesAsync</c> resolves them per scope).
/// </summary>
public sealed class OutboxProcessorTests
{
    private readonly Mock<IOutboxStore> _store = new(MockBehavior.Strict);
    private readonly Mock<INotificationSerializer> _serializer = new(MockBehavior.Strict);
    private readonly Mock<IDirectNotificationDispatcher> _dispatcher = new(MockBehavior.Strict);

    // A minimal notification used only as the deserializer's return value; its concrete shape is irrelevant here.
    private sealed record OutboxProcessorTestNotification : INotification;

    private static OutboxMessage Message(int attemptCount = 0, string type = "outbox.proc.test") => new(
        Id: Guid.NewGuid(),
        NotificationType: type,
        Payload: "{}"u8.ToArray(),
        CreatedAt: DateTime.UtcNow,
        Status: OutboxMessageStatus.InProgress,
        ProcessedAt: null,
        LastError: null,
        AttemptCount: attemptCount,
        NextRetryAt: null,
        TraceParent: null);

    private OutboxProcessor CreateProcessor(int maxRetryAttempts = 3)
    {
        var options = Options.Create(new OutboxProcessorOptions
        {
            // A long polling interval keeps the service blocked in Task.Delay after a single cycle, so a cycle never
            // re-runs while the test asserts. The test always stops the service before the delay elapses.
            PollingInterval = TimeSpan.FromMinutes(10),
            BatchSize = 100,
            MaxRetryAttempts = maxRetryAttempts
        });

        var scopeFactory = new OutboxScopeFactory(_store.Object, _serializer.Object, _dispatcher.Object);
        return new OutboxProcessor(NullLogger<OutboxProcessor>.Instance, scopeFactory, options);
    }

    /// <summary>
    ///     Starts the processor, awaits the gate (signalled from the terminal store call under test), then stops the
    ///     service. Guards against a hang if the expected terminal call never happens.
    /// </summary>
    private static async Task RunOneCycleAsync(OutboxProcessor processor, Task gate)
    {
        await processor.StartAsync(CancellationToken.None);
        try
        {
            var completed = await Task.WhenAny(gate, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().BeSameAs(gate, "the cycle should reach its terminal store call within the timeout");
            await gate; // surface any exception captured on the gate
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Outbox: successful dispatch marks the message processed")]
    public async Task SuccessfulDispatch_MarksAsProcessed()
    {
        // Arrange
        var message = Message();
        // Typed as INotification so the mock setup binds the non-generic Publish(INotification, ...) overload the
        // processor actually calls (a 'var' here would bind the generic Publish<T> overload and the strict mock would miss).
        INotification notification = new OutboxProcessorTestNotification();
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _store.Setup(s => s.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        _serializer.Setup(s => s.Deserialize(message.NotificationType, message.Payload))
            .Returns(notification);
        _dispatcher.Setup(d => d.Publish(notification, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _store.Setup(s => s.MarkAsProcessedAsync(message.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => processed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(), processed.Task);

        // Assert
        _store.Verify(s => s.MarkAsProcessedAsync(message.Id, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.IncrementAttemptAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsFailedAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "Outbox: handler throwing below the limit records a retry attempt (no dead-letter)")]
    public async Task HandlerThrows_BelowLimit_IncrementsAttempt_DoesNotDeadLetter()
    {
        // Arrange - first attempt: IncrementAttemptAsync returns 1, which is below MaxRetryAttempts (3).
        var message = Message();
        // Typed as INotification so the mock setup binds the non-generic Publish(INotification, ...) overload the
        // processor actually calls (a 'var' here would bind the generic Publish<T> overload and the strict mock would miss).
        INotification notification = new OutboxProcessorTestNotification();
        var handlerError = new InvalidOperationException("handler boom");
        var incremented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _store.Setup(s => s.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        _serializer.Setup(s => s.Deserialize(message.NotificationType, message.Payload))
            .Returns(notification);
        _dispatcher.Setup(d => d.Publish(notification, It.IsAny<CancellationToken>()))
            .ThrowsAsync(handlerError);
        _store.Setup(s => s.IncrementAttemptAsync(message.Id, It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1)
            .Callback(() => incremented.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(maxRetryAttempts: 3), incremented.Task);

        // Assert
        _store.Verify(s => s.IncrementAttemptAsync(message.Id, It.Is<string?>(e => e != null && e.Contains("handler boom")), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsFailedAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsProcessedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "Outbox: handler throwing at the retry limit dead-letters the message")]
    public async Task HandlerThrows_AtLimit_MarksAsFailed()
    {
        // Arrange - IncrementAttemptAsync returns the attempt count that reaches MaxRetryAttempts (3) -> dead-letter.
        var message = Message(attemptCount: 2);
        // Typed as INotification so the mock setup binds the non-generic Publish(INotification, ...) overload the
        // processor actually calls (a 'var' here would bind the generic Publish<T> overload and the strict mock would miss).
        INotification notification = new OutboxProcessorTestNotification();
        var handlerError = new InvalidOperationException("terminal boom");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _store.Setup(s => s.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        _serializer.Setup(s => s.Deserialize(message.NotificationType, message.Payload))
            .Returns(notification);
        _dispatcher.Setup(d => d.Publish(notification, It.IsAny<CancellationToken>()))
            .ThrowsAsync(handlerError);
        _store.Setup(s => s.IncrementAttemptAsync(message.Id, It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3); // == MaxRetryAttempts
        _store.Setup(s => s.MarkAsFailedAsync(message.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => failed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(maxRetryAttempts: 3), failed.Task);

        // Assert
        _store.Verify(s => s.IncrementAttemptAsync(message.Id, It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsFailedAsync(message.Id, It.Is<string?>(e => e != null && e.Contains("terminal boom")), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsProcessedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "Outbox: unknown notification type (deserialize returns null) fails immediately without retry")]
    public async Task UnknownNotificationType_MarksAsFailed_NoRetry()
    {
        // Arrange - serializer returns null for an unknown stable name; the message can never succeed -> fail at once.
        var message = Message(type: "outbox.proc.unknown");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _store.Setup(s => s.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        _serializer.Setup(s => s.Deserialize(message.NotificationType, message.Payload))
            .Returns((INotification?)null);
        _store.Setup(s => s.MarkAsFailedAsync(message.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => failed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(), failed.Task);

        // Assert
        _store.Verify(s => s.MarkAsFailedAsync(message.Id, It.Is<string?>(e => e != null && e.Contains(message.NotificationType)), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.IncrementAttemptAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsProcessedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        // The dispatcher must never be touched for an undeserializable message.
        _dispatcher.Verify(d => d.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "Outbox: corrupt payload (JsonException) dead-letters immediately with the real cause, no retry")]
    public async Task CorruptPayload_MarksAsFailed_WithRealCause_NoRetry()
    {
        // Arrange - a known type whose payload is corrupt: deserialize throws JsonException. This is deterministic,
        // so it must be dead-lettered immediately (NOT retried) and the recorded error must carry the real cause.
        var message = Message(type: "outbox.proc.corrupt");
        var jsonError = new JsonException("Unexpected token while reading outbox payload.");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _store.Setup(s => s.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { message });
        _serializer.Setup(s => s.Deserialize(message.NotificationType, message.Payload))
            .Throws(jsonError);
        _store.Setup(s => s.MarkAsFailedAsync(message.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => failed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(), failed.Task);

        // Assert - the JsonException path records the exception's own text (jsonEx.ToString()), not the generic
        // "Failed to deserialize" message used for the unknown-type (null) path.
        _store.Verify(s => s.MarkAsFailedAsync(message.Id, It.Is<string?>(e => e != null && e.Contains("Unexpected token while reading outbox payload.")), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.IncrementAttemptAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkAsProcessedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _dispatcher.Verify(d => d.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "Outbox: a store failure on the failure path does not abort the rest of the batch")]
    public async Task StoreFailureOnFailurePath_DoesNotAbortBatch()
    {
        // Arrange - two messages share one batch:
        //   #1 is corrupt (JsonException) and its MarkAsFailedAsync THROWS (the store itself is the failing dependency).
        //   #2 is a healthy message that must still be dispatched and processed despite #1's store failure.
        var corrupt = Message(type: "outbox.proc.corrupt");
        var healthy = Message(type: "outbox.proc.ok");
        // Typed as INotification so the mock setup binds the non-generic Publish(INotification, ...) overload the
        // processor actually calls (a 'var' here would bind the generic Publish<T> overload and the strict mock would miss).
        INotification notification = new OutboxProcessorTestNotification();
        var secondProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _store.Setup(s => s.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { corrupt, healthy });

        _serializer.Setup(s => s.Deserialize(corrupt.NotificationType, corrupt.Payload))
            .Throws(new JsonException("corrupt"));
        _store.Setup(s => s.MarkAsFailedAsync(corrupt.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store is down")); // store failure on the failure path

        _serializer.Setup(s => s.Deserialize(healthy.NotificationType, healthy.Payload))
            .Returns(notification);
        _dispatcher.Setup(d => d.Publish(notification, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _store.Setup(s => s.MarkAsProcessedAsync(healthy.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => secondProcessed.TrySetResult());

        // Act
        await RunOneCycleAsync(CreateProcessor(), secondProcessed.Task);

        // Assert - the store failure on message #1 was swallowed and message #2 still completed successfully.
        _store.Verify(s => s.MarkAsFailedAsync(corrupt.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkAsProcessedAsync(healthy.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    ///     A minimal <see cref="IServiceScopeFactory" /> that resolves the three outbox collaborators the processor
    ///     pulls from each scope (<see cref="IOutboxStore" />, <see cref="INotificationSerializer" />,
    ///     <see cref="IDirectNotificationDispatcher" />) and null for anything else.
    /// </summary>
    private sealed class OutboxScopeFactory(
        IOutboxStore store,
        INotificationSerializer serializer,
        IDirectNotificationDispatcher dispatcher) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new Provider(store, serializer, dispatcher));

        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() { }
        }

        private sealed class Provider(
            IOutboxStore store,
            INotificationSerializer serializer,
            IDirectNotificationDispatcher dispatcher) : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IOutboxStore)) return store;
                if (serviceType == typeof(INotificationSerializer)) return serializer;
                if (serviceType == typeof(IDirectNotificationDispatcher)) return dispatcher;
                return null;
            }
        }
    }
}
