using System.Runtime.CompilerServices;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Outbox flush ownership in <c>OutboxMode.Enabled</c>: every request settles what it and its finished nested
///     requests buffered — stored when it succeeds, discarded when it fails — and nothing else, however requests overlap in
///     one scope; a publish from outside any running request of the scope goes straight to the store.
/// </summary>
public sealed class OutboxFlushOwnershipTests
{
    [Fact(DisplayName = "Enabled mode: a notification published by a non-transactional command is persisted when it succeeds")]
    public async Task Plain_command_notifications_are_flushed_on_success()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            (await dispatcher.Send(new PublishingCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        }

        (await PendingAsync(provider)).Should().ContainSingle()
            .Which.NotificationType.Should().Be("ownership.published");
    }

    [Fact(DisplayName = "UseOutbox without a mode selects Enabled: a plain command's notification reaches the store without any unit of work")]
    public async Task UseOutbox_defaults_to_enabled()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseInMemoryStore()));
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboxOptions>>().Value.Mode.Should().Be(OutboxMode.Enabled);
        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new PublishingCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        (await PendingAsync(provider)).Should().ContainSingle();
    }

    [Fact(DisplayName = "Enabled mode: a notification published outside any request goes straight to the store")]
    public async Task Publish_outside_a_request_is_stored_immediately()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            await dispatcher.Publish(new PublishedEvent(), TestContext.Current.CancellationToken);
        }

        (await PendingAsync(provider)).Should().ContainSingle();
    }

    [Fact(DisplayName = "Enabled mode: a failed command's notifications are discarded, not persisted by the next command in the scope")]
    public async Task Failed_command_notifications_are_discarded()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            var act = () => dispatcher.Send(new PublishingCommand { ThrowAfterPublish = true });
            await act.Should().ThrowAsync<InvalidOperationException>();

            // Same scope (the default ScopeMode): the failed command's notification must not ride along.
            (await dispatcher.Send(new PublishingCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        }

        (await PendingAsync(provider)).Should().ContainSingle("only the successful command's notification is durable");
    }

    [Fact(DisplayName = "Enabled mode: notifications a handler buffered are stored even when the caller's token was cancelled as the handler returned")]
    public async Task Leftovers_are_stored_under_a_cancelled_token()
    {
        var services = new ServiceCollection();
        services.AddSingleton<CancellationTokenSource>();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        await using var provider = services.BuildServiceProvider();
        var cts = provider.GetRequiredService<CancellationTokenSource>();

        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            (await dispatcher.Send(new CancelAfterPublishCommand(), cts.Token)).IsSuccess.Should().BeTrue();
        }

        await using var check = provider.CreateAsyncScope();
        var store = check.ServiceProvider.GetRequiredService<IOutboxStore>();
        (await store.ClaimPendingAsync(10, CancellationToken.None)).Should().ContainSingle("the handler's work stands, so its notification must too");
    }

    [Fact(DisplayName = "A nested command whose post-handler fails does not get its notifications stored by the caller that swallowed the failure")]
    public async Task Nested_failure_does_not_leak_notifications_into_the_outer_request()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            (await dispatcher.Send(new OuterCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        }

        await using var check = provider.CreateAsyncScope();
        var pending = await check.ServiceProvider.GetRequiredService<IOutboxStore>().ClaimPendingAsync(10, CancellationToken.None);
        pending.Select(c => c.Message.NotificationType).Should().Equal("ownership.outer");
    }

    [Fact(DisplayName = "A nested stream that faults does not leave its notifications for the enclosing request to store")]
    public async Task Nested_stream_failure_discards_its_notifications()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new StreamingOuterCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        await using var check = provider.CreateAsyncScope();
        var pending = await check.ServiceProvider.GetRequiredService<IOutboxStore>().ClaimPendingAsync(10, CancellationToken.None);
        pending.Select(c => c.Message.NotificationType).Should().Equal("ownership.outer");
    }

    [Fact(DisplayName = "A notification published by a behavior of a nested request that fails is discarded with it")]
    public async Task Nested_behavior_notifications_are_discarded_with_the_request()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        services.AddTransient<IPipelineBehavior<InnerFailingCommand, CommandResult>, PublishingBehavior>();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new BehaviorOuterCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        await using var check = provider.CreateAsyncScope();
        var pending = await check.ServiceProvider.GetRequiredService<IOutboxStore>().ClaimPendingAsync(10, CancellationToken.None);
        pending.Select(c => c.Message.NotificationType).Should().Equal("ownership.outer");
    }

    [Fact(DisplayName = "A caller's notification published between the items of a nested stream it abandons is kept")]
    public async Task Interleaved_caller_notifications_survive_an_abandoned_nested_stream()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new InterleavingCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        await using var check = provider.CreateAsyncScope();
        var pending = await check.ServiceProvider.GetRequiredService<IOutboxStore>().ClaimPendingAsync(10, CancellationToken.None);
        pending.Select(c => c.Message.NotificationType).Should().BeEquivalentTo("ownership.outer", "ownership.inner");
    }

    [Fact(DisplayName = "With RollbackOnFailedResult = false, a failed command's notifications are saved with its changes")]
    public async Task Failed_result_notifications_are_kept_when_the_unit_of_work_commits_them()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseOutbox(o => o.Enabled().UseInMemoryStore())
            .UseUnitOfWork(_ => new RecordingUnitOfWork(), o => o.RollbackOnFailedResult = false));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FailingTransactionalCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeFalse();

        await using var check = provider.CreateAsyncScope();
        var pending = await check.ServiceProvider.GetRequiredService<IOutboxStore>().ClaimPendingAsync(10, CancellationToken.None);
        pending.Select(c => c.Message.NotificationType).Should().Equal("ownership.outer");
    }

    [Fact(DisplayName = "A request that succeeds while a sibling in the same scope is still running keeps its notifications when the sibling then fails")]
    public async Task Sibling_success_survives_a_failing_sibling()
    {
        await using var provider = BuildProvider(services => services.AddSingleton<OwnershipGates>());
        var gates = provider.GetRequiredService<OwnershipGates>();

        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            var failing = dispatcher.Send(new GatedFailingCommand(), TestContext.Current.CancellationToken);
            await gates.FailingStarted.Task;

            (await dispatcher.Send(new PublishingCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

            gates.Release.SetResult();
            await ((Func<Task>)(() => failing)).Should().ThrowAsync<InvalidOperationException>();
        }

        (await PendingAsync(provider)).Select(m => m.NotificationType).Should().Equal("ownership.published");
    }

    [Fact(DisplayName = "A command sent while its caller enumerates a stream is stored even though the caller then stops enumerating")]
    public async Task Command_sent_from_a_stream_consumer_is_stored()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            await foreach (var _ in dispatcher.Stream(new InterleavingStream(), TestContext.Current.CancellationToken))
            {
                (await dispatcher.Send(new PublishingCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
                break;
            }
        }

        (await PendingAsync(provider)).Select(m => m.NotificationType).Should().Equal(
            new[] { "ownership.published" }, "the command's notification stands; only the abandoned stream's own is discarded");
    }

    [Fact(DisplayName = "A publish by a stream's consumer between items goes straight to the store")]
    public async Task Publish_between_stream_items_is_stored_immediately()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            await foreach (var _ in dispatcher.Stream(new InterleavingStream(), TestContext.Current.CancellationToken))
            {
                await dispatcher.Publish(new InnerEvent(), TestContext.Current.CancellationToken);
                scope.ServiceProvider.GetRequiredService<ScopedOutbox>().Count.Should().Be(1, "only the stream's own notification is buffered");
                break;
            }
        }

        (await PendingAsync(provider)).Select(m => m.NotificationType).Should().Equal("ownership.inner");
    }

    [Fact(DisplayName = "In a long-lived scope holding a stream open, each command's notifications are stored when it succeeds and the buffer does not grow")]
    public async Task Open_stream_does_not_hold_back_later_commands()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            var outbox = scope.ServiceProvider.GetRequiredService<ScopedOutbox>();
            await using var liveFeed = dispatcher.Stream(new InterleavingStream(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            (await liveFeed.MoveNextAsync()).Should().BeTrue();

            for (var i = 0; i < 3; i++)
            {
                (await dispatcher.Send(new PublishingCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
                outbox.Count.Should().Be(1, "only the open stream's own notification is still buffered");
            }
        }

        (await PendingAsync(provider)).Select(m => m.NotificationType).Should().Equal("ownership.published", "ownership.published", "ownership.published");
    }

    [Fact(DisplayName = "Under ExecutionScopeMode.New a nested request runs in a scope of its own and stores its own notifications")]
    public async Task Nested_request_in_a_new_scope_stores_its_notifications()
    {
        await using var provider = BuildProvider(services => services.Configure<DispatcherOptions>(o => o.ScopeMode = ExecutionScopeMode.New));
        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new NestingCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        (await PendingAsync(provider)).Select(m => m.NotificationType).Should().BeEquivalentTo("ownership.outer", "ownership.published");
    }

    [Fact(DisplayName = "A nested request its caller does not wait for settles its own notifications: its later failure never takes the caller's")]
    public async Task Unawaited_nested_failure_keeps_the_callers_notifications()
    {
        await using var provider = BuildProvider(services => services.AddSingleton<OwnershipGates>());
        var gates = provider.GetRequiredService<OwnershipGates>();

        await using (var scope = provider.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FireAndForgetCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

            gates.Release.SetResult();
            await ((Func<Task>)(() => gates.Nested!)).Should().ThrowAsync<InvalidOperationException>();
        }

        (await PendingAsync(provider)).Select(m => m.NotificationType).Should().Equal("ownership.outer");
    }

    [Fact(DisplayName = "A retried command whose CommandCompleted subscriber failed once stores its notifications once, not once per attempt")]
    public async Task Failed_completed_publish_discards_the_attempts_notifications()
    {
        await using var provider = BuildProvider(
            services =>
            {
                services.AddSingleton<OwnershipGates>();
                services.AddTransient<INotificationHandler<CommandCompletedNotification>, FailOnceCompletedSubscriber>();
            },
            b => b.UseResilience(o => o.BaseDelay = TimeSpan.Zero));

        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new RetryablePublishingCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        provider.GetRequiredService<OwnershipGates>().CompletedCalls.Should().Be(2, "the first attempt failed at its CommandCompleted subscriber and was retried");
        (await PendingAsync(provider)).Select(m => m.NotificationType).Should().Equal("ownership.published");
    }

    [Fact(DisplayName = "A retried stream that failed before its first item stores only the successful attempt's notifications")]
    public async Task Retried_stream_stores_one_attempts_notifications()
    {
        await using var provider = BuildProvider(
            services => services.AddSingleton<OwnershipGates>(),
            b => b.UseResilience(o =>
            {
                o.MaxRetries = 2;
                o.BaseDelay = TimeSpan.Zero;
            }));

        await using (var scope = provider.CreateAsyncScope())
        {
            var items = new List<int>();
            await foreach (var item in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(new RetryingNotifyingStream(), TestContext.Current.CancellationToken))
                items.Add(item);
            items.Should().Equal(1);
        }

        provider.GetRequiredService<OwnershipGates>().StreamAttempts.Should().Be(2);
        (await PendingAsync(provider)).Select(m => m.NotificationType).Should().Equal("ownership.stream");
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection> configureServices, Action<ICqrsBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        configureServices(services);
        services.AddCqrsGenerated(b =>
        {
            b.UseOutbox(o => o.Enabled().UseInMemoryStore());
            configure?.Invoke(b);
        });
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        return services.BuildServiceProvider();
    }

    private static async Task<OutboxMessage[]> PendingAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        return (await store.ClaimPendingAsync(100, CancellationToken.None)).Select(c => c.Message).ToArray();
    }

    // Private, so the generator does not register it for every command of the test assembly: registered by hand.
    private sealed class FailOnceCompletedSubscriber(OwnershipGates gates) : INotificationHandler<CommandCompletedNotification>
    {
        public Task Handle(CommandCompletedNotification notification, CancellationToken cancellationToken)
            => gates.CompletedCall() == 1 ? throw new InvalidOperationException("audit store unavailable") : Task.CompletedTask;
    }
}

/// <summary>The signals the overlapping-request tests coordinate through, instead of timing.</summary>
public sealed class OwnershipGates
{
    private int _completedCalls;
    private int _streamAttempts;

    public TaskCompletionSource FailingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<CommandResult>? Nested { get; set; }
    public int CompletedCalls => Volatile.Read(ref _completedCalls);
    public int StreamAttempts => Volatile.Read(ref _streamAttempts);

    public int CompletedCall() => Interlocked.Increment(ref _completedCalls);
    public int StreamAttempt() => Interlocked.Increment(ref _streamAttempts);
}

public sealed class GatedFailingCommand : CommandBase;

public sealed class GatedFailingCommandHandler(ICqrsDispatcher dispatcher, OwnershipGates gates) : ICommandHandler<GatedFailingCommand>
{
    public async Task<CommandResult> Handle(GatedFailingCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new InnerEvent(), cancellationToken);
        gates.FailingStarted.TrySetResult();
        await gates.Release.Task;
        throw new InvalidOperationException("failed after its sibling succeeded");
    }
}

public sealed class FireAndForgetCommand : CommandBase;

public sealed class FireAndForgetCommandHandler(ICqrsDispatcher dispatcher, OwnershipGates gates) : ICommandHandler<FireAndForgetCommand>
{
    public async Task<CommandResult> Handle(FireAndForgetCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new OuterEvent(), cancellationToken);
        gates.Nested = dispatcher.Send(new GatedFailingCommand(), cancellationToken);
        await gates.FailingStarted.Task;
        return CommandResult.FromSuccess();
    }
}

public sealed class NestingCommand : CommandBase;

public sealed class NestingCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<NestingCommand>
{
    public async Task<CommandResult> Handle(NestingCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new OuterEvent(), cancellationToken);
        return await dispatcher.Send(new PublishingCommand(), cancellationToken);
    }
}

public sealed class RetryablePublishingCommand : CommandBase, IRetryableRequest;

public sealed class RetryablePublishingCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<RetryablePublishingCommand>
{
    public async Task<CommandResult> Handle(RetryablePublishingCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new PublishedEvent(), cancellationToken);
        return CommandResult.FromSuccess();
    }
}

public sealed class RetryingNotifyingStream : StreamRequestBase<int>, IRetryableRequest;

public sealed class RetryingNotifyingStreamHandler(ICqrsDispatcher dispatcher, OwnershipGates gates) : IStreamRequestHandler<RetryingNotifyingStream, int>
{
    public async IAsyncEnumerable<int> Handle(RetryingNotifyingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new StreamEvent(), cancellationToken);
        if (gates.StreamAttempt() == 1) throw new InvalidOperationException("failed before its first item");
        yield return 1;
    }
}

public sealed class PublishingCommand : CommandBase
{
    public bool ThrowAfterPublish { get; init; }
}

public sealed class PublishingCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<PublishingCommand>
{
    public async Task<CommandResult> Handle(PublishingCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new PublishedEvent(), cancellationToken);
        if (command.ThrowAfterPublish) throw new InvalidOperationException("after publish");
        return CommandResult.FromSuccess();
    }
}

public sealed class CancelAfterPublishCommand : CommandBase;

public sealed class CancelAfterPublishCommandHandler(ICqrsDispatcher dispatcher, CancellationTokenSource cts) : ICommandHandler<CancelAfterPublishCommand>
{
    public async Task<CommandResult> Handle(CancelAfterPublishCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new PublishedEvent(), cancellationToken);
        await cts.CancelAsync();
        return CommandResult.FromSuccess();
    }
}

// The notification the publishing fixtures here send, with its handler right below: an outbox message is stored per
// subscribed handler, so every count in these tests rests on this file's own subscriptions.
[NotificationName("ownership.published")]
public sealed record PublishedEvent : INotification;

public sealed class PublishedEventHandler : INotificationHandler<PublishedEvent>
{
    public Task Handle(PublishedEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

[NotificationName("ownership.outer")]
public sealed record OuterEvent : INotification;

[NotificationName("ownership.inner")]
public sealed record InnerEvent : INotification;

public sealed class OuterEventHandler : INotificationHandler<OuterEvent>
{
    public Task Handle(OuterEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class InnerEventHandler : INotificationHandler<InnerEvent>
{
    public Task Handle(InnerEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class OuterCommand : CommandBase;

public sealed class OuterCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<OuterCommand>
{
    public async Task<CommandResult> Handle(OuterCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new OuterEvent(), cancellationToken);
        try
        {
            await dispatcher.Send(new InnerCommand(), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The inner request failed; this handler decides its own work still stands.
        }

        return CommandResult.FromSuccess();
    }
}

[ThrowingPostHandler]
public sealed class InnerCommand : CommandBase;

public sealed class InnerCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<InnerCommand>
{
    public async Task<CommandResult> Handle(InnerCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new InnerEvent(), cancellationToken);
        return CommandResult.FromSuccess();
    }
}

[NotificationName("ownership.stream")]
public sealed record StreamEvent : INotification;

public sealed class StreamEventHandler : INotificationHandler<StreamEvent>
{
    public Task Handle(StreamEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FaultingNotifyingStream : StreamRequestBase<int>;

public sealed class FaultingNotifyingStreamHandler(ICqrsDispatcher dispatcher) : IStreamRequestHandler<FaultingNotifyingStream, int>
{
    public async IAsyncEnumerable<int> Handle(FaultingNotifyingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new StreamEvent(), cancellationToken);
        yield return 1;
        throw new InvalidOperationException("stream boom");
    }
}

public sealed class StreamingOuterCommand : CommandBase;

public sealed class StreamingOuterCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<StreamingOuterCommand>
{
    public async Task<CommandResult> Handle(StreamingOuterCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new OuterEvent(), cancellationToken);
        try
        {
            await foreach (var _ in dispatcher.Stream(new FaultingNotifyingStream(), cancellationToken)) { }
        }
        catch (InvalidOperationException)
        {
        }

        return CommandResult.FromSuccess();
    }
}

public sealed class InnerFailingCommand : CommandBase;

public sealed class InnerFailingCommandHandler : ICommandHandler<InnerFailingCommand>
{
    public Task<CommandResult> Handle(InnerFailingCommand command, CancellationToken cancellationToken)
        => throw new InvalidOperationException("inner boom");
}

public sealed class PublishingBehavior(ICqrsDispatcher dispatcher) : IPipelineBehavior<InnerFailingCommand, CommandResult>
{
    public async Task<CommandResult> Handle(InnerFailingCommand request, RequestHandlerDelegate<CommandResult> next, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new InnerEvent(), cancellationToken);
        return await next(cancellationToken);
    }
}

public sealed class BehaviorOuterCommand : CommandBase;

public sealed class BehaviorOuterCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<BehaviorOuterCommand>
{
    public async Task<CommandResult> Handle(BehaviorOuterCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new OuterEvent(), cancellationToken);
        try
        {
            await dispatcher.Send(new InnerFailingCommand(), cancellationToken);
        }
        catch (InvalidOperationException)
        {
        }

        return CommandResult.FromSuccess();
    }
}

public sealed class InterleavingStream : StreamRequestBase<int>;

public sealed class InterleavingStreamHandler(ICqrsDispatcher dispatcher) : IStreamRequestHandler<InterleavingStream, int>
{
    public async IAsyncEnumerable<int> Handle(InterleavingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new StreamEvent(), cancellationToken);
        yield return 1;
        yield return 2;
    }
}

public sealed class InterleavingCommand : CommandBase;

public sealed class InterleavingCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<InterleavingCommand>
{
    public async Task<CommandResult> Handle(InterleavingCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new OuterEvent(), cancellationToken);
        await foreach (var _ in dispatcher.Stream(new InterleavingStream(), cancellationToken))
        {
            // The caller's own work between the stream's items, then it stops enumerating early.
            await dispatcher.Publish(new InnerEvent(), cancellationToken);
            break;
        }

        return CommandResult.FromSuccess();
    }
}

public sealed class FailingTransactionalCommand : CommandBase, ITransactionalCommand
{
    public System.Data.IsolationLevel IsolationLevel { get; set; } = System.Data.IsolationLevel.Unspecified;
}

public sealed class FailingTransactionalCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<FailingTransactionalCommand>
{
    public async Task<CommandResult> Handle(FailingTransactionalCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new OuterEvent(), cancellationToken);
        return CommandResult.FromError("declined, but recorded");
    }
}

