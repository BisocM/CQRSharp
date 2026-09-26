using System.Runtime.CompilerServices;
using CQRSharp.Core.Notifications;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Locks the request lifecycle contract: once <c>*Initiated</c> is published the request always reaches exactly one
///     terminal notification, <c>*Failed</c> for every failure (cancellation and timeouts included) and its outcome-aware
///     post-handlers, whichever stage throws; the failure path runs under no cancellation token, and nothing on it can
///     replace the exception the caller sees. Value-returning commands and streams follow the same contract.
/// </summary>
public sealed class RequestLifecycleContractTests
{
    [Fact(DisplayName = "A throwing pre-handler still produces CommandFailed and reaches the post-handlers")]
    public async Task PreHandler_failure_is_a_terminal_failure()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<LifecycleProbe>();

        var act = () => dispatcher.Send(new GuardedCommand());

        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).WithMessage("denied by pre-handler");
        probe.HandlerRan.Should().BeFalse();
        probe.FailedNotifications.Should().ContainSingle()
            .Which.Should().BeOfType<UnauthorizedAccessException>();
        probe.Outcomes.Should().ContainSingle().Which.Threw.Should().BeTrue();
    }

    [Fact(DisplayName = "A faulting CommandFailed subscriber never masks the handler's exception")]
    public async Task Failed_subscriber_cannot_mask_the_original_exception()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<LifecycleProbe>();
        probe.FailedSubscriberThrows = true;

        var act = () => dispatcher.Send(new ExplodingCommand());

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("handler exploded");
        probe.Outcomes.Should().ContainSingle("the post-handlers still run after a faulting subscriber")
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact(DisplayName = "A faulted stream reaches the outcome-aware post-handlers with its exception")]
    public async Task Stream_failure_reaches_post_handlers()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<LifecycleProbe>();

        var items = new List<int>();
        var act = async () =>
        {
            await foreach (var item in dispatcher.Stream(new ExplodingStream()))
                items.Add(item);
        };

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("stream exploded");
        items.Should().Equal(1);
        probe.Outcomes.Should().ContainSingle().Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact(DisplayName = "A post-handler that fails after a successful handler still ends the lifecycle with CommandFailedNotification")]
    public async Task Post_handler_failure_publishes_the_failed_notification()
    {
        var recorder = new LifecycleRecorder();
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        recorder.Subscribe(services);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var act = () => dispatcher.Send(new FailingPostHandlerCommand());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("post-handler boom");
        recorder.Published.OfType<CommandInitiatedNotification>().Should().ContainSingle();
        recorder.Published.OfType<CommandFailedNotification>().Should().ContainSingle()
            .Which.Exception.Message.Should().Be("post-handler boom");
        recorder.Published.OfType<CommandCompletedNotification>().Should().BeEmpty();
    }

    [Fact(DisplayName = "A stream that fails with a cancellation its consumer never asked for publishes StreamFailed")]
    public async Task Stream_failing_with_a_foreign_cancellation_publishes_the_failed_notification()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = BuildRecorded(recorder);
        await using var scope = provider.CreateAsyncScope();

        var items = new List<int>();
        var act = async () =>
        {
            await foreach (var item in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(new DependencyTimedOutStream()))
                items.Add(item);
        };

        await act.Should().ThrowAsync<TaskCanceledException>().WithMessage("dependency timed out");
        items.Should().Equal(1);
        recorder.Published.OfType<StreamInitiatedNotification<int>>().Should().ContainSingle();
        var failed = recorder.Published.OfType<StreamFailedNotification<int>>().Should().ContainSingle().Subject;
        failed.ItemsYielded.Should().Be(1);
        failed.Exception.Should().BeOfType<TaskCanceledException>();
    }

    [Fact(DisplayName = "A stream stopped by UseTimeout's deadline publishes StreamFailed, and its consumer receives the timeout")]
    public async Task Stream_past_its_deadline_publishes_the_failed_notification()
    {
        var clock = new FakeTimeProvider();
        var recorder = new LifecycleRecorder();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddCqrsGenerated(b => b.UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(5)));
        recorder.Subscribe(services);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await using var enumerator = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Stream(new StalledStream(), TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await enumerator.MoveNextAsync()).Should().BeTrue();

        // The handler now waits on its token; the deadline passes while it does.
        var next = enumerator.MoveNextAsync();
        clock.Advance(TimeSpan.FromSeconds(6));

        await FluentActions.Awaiting(async () => await next).Should().ThrowAsync<TimeoutException>();
        var failed = recorder.Published.OfType<StreamFailedNotification<int>>().Should().ContainSingle().Subject;
        failed.ItemsYielded.Should().Be(1);
        failed.Exception.Should().BeAssignableTo<OperationCanceledException>();
        recorder.Published.OfType<StreamCompletedNotification<int>>().Should().BeEmpty();
    }

    [Fact(DisplayName = "A cancelled command still reaches a token-honouring CommandFailed subscriber and post-handler")]
    public async Task Cancelled_command_audits_its_failure_under_no_token()
    {
        var audit = new CancellationAudit();
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton(audit);
        services.AddTransient<INotificationHandler<CommandFailedNotification>, TokenHonouringFailedSubscriber>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new CallerCancelledCommand(), audit.Caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        audit.Entries.Should().BeEquivalentTo(["failed notification", "post-handler"]);
    }

    [Fact(DisplayName = "A value-returning command publishes CommandInitiated and CommandCompleted with its result")]
    public async Task Value_returning_command_publishes_its_lifecycle()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = BuildRecorded(recorder);
        await using var scope = provider.CreateAsyncScope();
        var command = new IssueApiKeyCommand();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(command, TestContext.Current.CancellationToken);

        result.Value.Should().Be("key-1");
        recorder.Published.OfType<CommandInitiatedNotification>().Should().ContainSingle().Which.Command.Should().BeSameAs(command);
        var completed = recorder.Published.OfType<CommandCompletedNotification>().Should().ContainSingle().Subject;
        completed.Command.Should().BeSameAs(command);
        completed.CommandName.Should().Be(nameof(IssueApiKeyCommand));
        completed.Result.Should().BeSameAs(result);
        recorder.Published.OfType<CommandFailedNotification>().Should().BeEmpty();
    }

    [Fact(DisplayName = "A value-returning command that throws publishes CommandFailed")]
    public async Task Failing_value_returning_command_publishes_the_failed_notification()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = BuildRecorded(recorder);
        await using var scope = provider.CreateAsyncScope();
        var command = new IssueApiKeyCommand { Throw = true };

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(command);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("key store offline");
        recorder.Published.OfType<CommandFailedNotification>().Should().ContainSingle()
            .Which.Command.Should().BeSameAs(command);
        recorder.Published.OfType<CommandCompletedNotification>().Should().BeEmpty();
    }

    [Fact(DisplayName = "A CommandCompleted subscriber receives value-returning commands through the built-in dispatcher")]
    public async Task Completed_subscriber_sees_value_returning_commands()
    {
        var seen = new List<CommandCompletedNotification>();
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton(seen);
        services.AddTransient<INotificationHandler<CommandCompletedNotification>, CompletedRecorder>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new IssueApiKeyCommand(), TestContext.Current.CancellationToken);

        seen.Should().ContainSingle().Which.Result.Should().BeSameAs(result);
    }

    [Fact(DisplayName = "A query publishes QueryInitiated, then QueryCompleted with the result its caller receives")]
    public async Task Query_publishes_its_lifecycle()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = BuildRecorded(recorder);
        await using var scope = provider.CreateAsyncScope();
        var query = new LifecycleQuery();

        var answer = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(query, TestContext.Current.CancellationToken);

        recorder.Published.Should().SatisfyRespectively(
            first => first.Should().BeOfType<QueryInitiatedNotification<LifecycleAnswer>>().Which.Query.Should().BeSameAs(query),
            second => second.Should().BeOfType<QueryCompletedNotification<LifecycleAnswer>>().Which.Result.Should().BeSameAs(answer));
    }

    [Fact(DisplayName = "A query whose handler throws publishes QueryInitiated, then QueryFailed with the exception its caller receives")]
    public async Task Failing_query_publishes_the_failed_notification()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = BuildRecorded(recorder);
        await using var scope = provider.CreateAsyncScope();
        var query = new LifecycleQuery { Throw = true };

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(query);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("query exploded")).Which;
        recorder.Published.Should().SatisfyRespectively(
            first => first.Should().BeOfType<QueryInitiatedNotification<LifecycleAnswer>>(),
            second =>
            {
                var failed = second.Should().BeOfType<QueryFailedNotification<LifecycleAnswer>>().Subject;
                failed.Query.Should().BeSameAs(query);
                failed.Exception.Should().BeSameAs(thrown);
            });
    }

    [Fact(DisplayName = "A query that carries ITransactionalCommand is still a query: it returns its value, publishes Query* notifications and commits")]
    public async Task Query_with_the_transactional_command_marker_keeps_the_query_lifecycle()
    {
        var recorder = new LifecycleRecorder();
        var unitOfWork = new RecordingUnitOfWork();
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseUnitOfWork(_ => unitOfWork));
        recorder.Subscribe(services);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var query = new TransactionallyMarkedQuery();

        var answer = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(query, TestContext.Current.CancellationToken);

        answer.Value.Should().Be("written");
        unitOfWork.Commits.Should().Be(1);
        recorder.Published.Should().SatisfyRespectively(
            first => first.Should().BeOfType<QueryInitiatedNotification<LifecycleAnswer>>().Which.Query.Should().BeSameAs(query),
            second => second.Should().BeOfType<QueryCompletedNotification<LifecycleAnswer>>().Which.Result.Should().BeSameAs(answer));
    }

    [Fact(DisplayName = "A query that implements ICommandMarker itself is not a command: its failure publishes QueryFailed, never a Command* notification")]
    public async Task Query_with_the_command_marker_keeps_the_query_lifecycle()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = BuildRecorded(recorder);
        await using var scope = provider.CreateAsyncScope();

        var answer = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new CommandMarkedQuery(), TestContext.Current.CancellationToken);
        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new CommandMarkedQuery { Throw = true });

        answer.Value.Should().Be("marked");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("marked query exploded");
        recorder.Published.Select(n => n.GetType()).Should().Equal(
            typeof(QueryInitiatedNotification<LifecycleAnswer>), typeof(QueryCompletedNotification<LifecycleAnswer>),
            typeof(QueryInitiatedNotification<LifecycleAnswer>), typeof(QueryFailedNotification<LifecycleAnswer>));
    }

    [Fact(DisplayName = "A stream enumerated to its end publishes StreamInitiated, then StreamCompleted with the item count, and reaches its post-handlers")]
    public async Task Completed_stream_publishes_its_lifecycle()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = BuildRecorded(recorder, probed: true);
        await using var scope = provider.CreateAsyncScope();
        var request = new CountingStream { Count = 3 };

        var items = new List<int>();
        await foreach (var item in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(request, TestContext.Current.CancellationToken))
            items.Add(item);

        items.Should().Equal(1, 2, 3);
        recorder.Published.Should().SatisfyRespectively(
            first => first.Should().BeOfType<StreamInitiatedNotification<int>>().Which.Request.Should().BeSameAs(request),
            second =>
            {
                var completed = second.Should().BeOfType<StreamCompletedNotification<int>>().Subject;
                completed.Request.Should().BeSameAs(request);
                completed.ItemsYielded.Should().Be(3);
            });
        scope.ServiceProvider.GetRequiredService<LifecycleProbe>().Outcomes.Should().ContainSingle().Which.Threw.Should().BeFalse();
    }

    [Fact(DisplayName = "A stream that faults publishes StreamFailed with the items it yielded and its exception, never StreamCompleted")]
    public async Task Faulted_stream_publishes_the_failed_notification()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = BuildRecorded(recorder);
        await using var scope = provider.CreateAsyncScope();

        var act = async () =>
        {
            await foreach (var _ in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(new ExplodingStream()))
            {
            }
        };

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("stream exploded")).Which;
        recorder.Published.Should().SatisfyRespectively(
            first => first.Should().BeOfType<StreamInitiatedNotification<int>>(),
            second =>
            {
                var failed = second.Should().BeOfType<StreamFailedNotification<int>>().Subject;
                failed.ItemsYielded.Should().Be(1);
                failed.Exception.Should().BeSameAs(thrown);
            });
    }

    [Fact(DisplayName = "A post-handler that fails after a stream ran to its end publishes StreamFailed, not StreamCompleted, and its consumer receives that failure")]
    public async Task Stream_post_handler_failure_publishes_the_failed_notification()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = BuildRecorded(recorder);
        await using var scope = provider.CreateAsyncScope();

        var items = new List<int>();
        var act = async () =>
        {
            await foreach (var item in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(new PostHandlerFailingStream()))
                items.Add(item);
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("post-handler boom");
        items.Should().Equal(1, 2);
        var failed = recorder.Published.OfType<StreamFailedNotification<int>>().Should().ContainSingle().Subject;
        failed.ItemsYielded.Should().Be(2);
        failed.Exception.Message.Should().Be("post-handler boom");
        recorder.Published.OfType<StreamCompletedNotification<int>>().Should().BeEmpty();
    }

    [Fact(DisplayName = "A stream its consumer stops enumerating early ends with no terminal notification and runs no post-handlers")]
    public async Task Abandoned_stream_ends_without_a_terminal_notification()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = BuildRecorded(recorder, probed: true);
        await using var scope = provider.CreateAsyncScope();

        await foreach (var _ in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(new CountingStream { Count = 3 }, TestContext.Current.CancellationToken))
            break;

        recorder.Published.Should().ContainSingle().Which.Should().BeOfType<StreamInitiatedNotification<int>>();
        scope.ServiceProvider.GetRequiredService<LifecycleProbe>().Outcomes.Should().BeEmpty();
    }

    [Theory(DisplayName = "Cancelling a stream through the token given to Stream or the one it is enumerated with reaches the handler and publishes StreamFailed")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_stream_publishes_the_failed_notification(bool throughEnumeration)
    {
        var recorder = new LifecycleRecorder();
        await using var provider = BuildRecorded(recorder);
        await using var scope = provider.CreateAsyncScope();
        using var dispatchToken = new CancellationTokenSource();
        using var enumerationToken = new CancellationTokenSource();
        var request = new CancellableStream();

        var items = new List<int>();
        var act = async () =>
        {
            var stream = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(request, dispatchToken.Token);
            await foreach (var item in stream.WithCancellation(enumerationToken.Token))
            {
                items.Add(item);
                await (throughEnumeration ? enumerationToken : dispatchToken).CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        items.Should().Equal(1);
        request.HandlerTokenCancelled.Should().BeTrue("the handler observes the cancellation through its own token");
        var failed = recorder.Published.OfType<StreamFailedNotification<int>>().Should().ContainSingle().Subject;
        failed.ItemsYielded.Should().Be(1);
        failed.Exception.Should().BeAssignableTo<OperationCanceledException>();
        recorder.Published.OfType<StreamCompletedNotification<int>>().Should().BeEmpty();
    }

    // Private, so the generator does not auto-register them: an assembly-wide subscriber would switch the fast path off
    // for every other test's commands. The tests that want them register them by hand.
    private sealed class TokenHonouringFailedSubscriber(CancellationAudit audit) : INotificationHandler<CommandFailedNotification>
    {
        public Task Handle(CommandFailedNotification notification, CancellationToken cancellationToken)
        {
            if (notification.Command is not CallerCancelledCommand) return Task.CompletedTask;

            cancellationToken.ThrowIfCancellationRequested();
            audit.Add("failed notification");
            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleFailedSubscriber(LifecycleProbe probe) : INotificationHandler<CommandFailedNotification>
    {
        public Task Handle(CommandFailedNotification notification, CancellationToken cancellationToken)
        {
            probe.FailedNotifications.Add(notification.Exception);
            if (probe.FailedSubscriberThrows) throw new ApplicationException("subscriber fault");
            return Task.CompletedTask;
        }
    }

    private sealed class CompletedRecorder(List<CommandCompletedNotification> seen) : INotificationHandler<CommandCompletedNotification>
    {
        public Task Handle(CommandCompletedNotification notification, CancellationToken cancellationToken)
        {
            if (notification.Command is IssueApiKeyCommand) seen.Add(notification);
            return Task.CompletedTask;
        }
    }

    // The recorder subscribes to the lifecycle of the commands, queries and streams these tests send; probed, the
    // outcome-aware post-handlers record into the scope's probe as well.
    private static ServiceProvider BuildRecorded(LifecycleRecorder recorder, bool probed = false)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        recorder.Subscribe(services);
        if (probed) services.AddScoped<LifecycleProbe>();
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddScoped<LifecycleProbe>();
        services.AddTransient<INotificationHandler<CommandFailedNotification>, LifecycleFailedSubscriber>();
        return services.BuildServiceProvider();
    }
}

/// <summary>
///     Per-scope record of what the fixtures below observed. Only these tests register it; without it the fixtures'
///     attributes record nothing, and no subscriber of theirs is registered for the rest of the assembly.
/// </summary>
public sealed class LifecycleProbe
{
    public bool HandlerRan { get; set; }
    public bool FailedSubscriberThrows { get; set; }
    public List<Exception> FailedNotifications { get; } = [];
    public List<RequestOutcome> Outcomes { get; } = [];
}

public sealed class DenyAttribute : Attribute, IPreHandlerAttribute
{
    public int PreHandlerExecutionPriority => 0;

    public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => throw new UnauthorizedAccessException("denied by pre-handler");
}

public sealed class RecordOutcomeAttribute : Attribute, IPostHandlerAttribute
{
    public int PostHandlerExecutionPriority => 0;

    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        serviceProvider.GetService<LifecycleProbe>()?.Outcomes.Add(outcome);
        return Task.CompletedTask;
    }
}

[Deny]
[RecordOutcome]
public sealed class GuardedCommand : CommandBase;

public sealed class GuardedCommandHandler(IServiceProvider services) : ICommandHandler<GuardedCommand>
{
    public Task<CommandResult> Handle(GuardedCommand command, CancellationToken cancellationToken)
    {
        if (services.GetService<LifecycleProbe>() is { } probe) probe.HandlerRan = true;
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

[RecordOutcome]
public sealed class ExplodingCommand : CommandBase;

public sealed class ExplodingCommandHandler : ICommandHandler<ExplodingCommand>
{
    public Task<CommandResult> Handle(ExplodingCommand command, CancellationToken cancellationToken)
        => throw new InvalidOperationException("handler exploded");
}

[RecordOutcome]
public sealed class ExplodingStream : StreamRequestBase<int>;

public sealed class ExplodingStreamHandler : IStreamRequestHandler<ExplodingStream, int>
{
    public async IAsyncEnumerable<int> Handle(ExplodingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        throw new InvalidOperationException("stream exploded");
    }
}

[ThrowingPostHandler]
public sealed class FailingPostHandlerCommand : CommandBase;

public sealed class FailingPostHandlerCommandHandler : ICommandHandler<FailingPostHandlerCommand>
{
    public Task<CommandResult> Handle(FailingPostHandlerCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

public sealed class DependencyTimedOutStream : StreamRequestBase<int>;

public sealed class DependencyTimedOutStreamHandler : IStreamRequestHandler<DependencyTimedOutStream, int>
{
    public async IAsyncEnumerable<int> Handle(DependencyTimedOutStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        // What an HttpClient timeout looks like: a cancellation nobody requested through the stream's own token.
        throw new TaskCanceledException("dependency timed out");
    }
}

public sealed class StalledStream : StreamRequestBase<int>;

public sealed class StalledStreamHandler : IStreamRequestHandler<StalledStream, int>
{
    public async IAsyncEnumerable<int> Handle(StalledStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield return 2;
    }
}

/// <summary>What a cancelled command's failure path reached, and the caller's token source the command cancels.</summary>
public sealed class CancellationAudit
{
    private readonly List<string> _entries = [];

    public CancellationTokenSource Caller { get; } = new();

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries) return _entries.ToArray();
        }
    }

    public void Add(string entry)
    {
        lock (_entries) _entries.Add(entry);
    }
}

public sealed class TokenHonouringAuditAttribute : Attribute, IPostHandlerAttribute
{
    public int PostHandlerExecutionPriority => 0;

    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        serviceProvider.GetService<CancellationAudit>()?.Add("post-handler");
        return Task.CompletedTask;
    }
}

// The caller gives up while the handler runs: the handler observes its own, now cancelled, token.
[TokenHonouringAudit]
public sealed class CallerCancelledCommand : CommandBase;

public sealed class CallerCancelledCommandHandler(CancellationAudit audit) : ICommandHandler<CallerCancelledCommand>
{
    public Task<CommandResult> Handle(CallerCancelledCommand command, CancellationToken cancellationToken)
    {
        audit.Caller.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

/// <summary>A command that mints a secret: the kind a value-returning command exists for, and the kind to audit.</summary>
public sealed class IssueApiKeyCommand : ResultCommandBase<string>
{
    public bool Throw { get; init; }
}

public sealed class IssueApiKeyCommandHandler : IResultCommandHandler<IssueApiKeyCommand, string>
{
    public Task<CommandResult<string>> Handle(IssueApiKeyCommand command, CancellationToken cancellationToken)
        => command.Throw
            ? throw new InvalidOperationException("key store offline")
            : Task.FromResult(CommandResult<string>.FromSuccess("key-1"));
}

public sealed record LifecycleAnswer(string Value);

// A result type only this file's queries answer, so Query*Notification<LifecycleAnswer> is only ever theirs.
public sealed class LifecycleQuery : QueryBase<LifecycleAnswer>
{
    public bool Throw { get; init; }
}

public sealed class LifecycleQueryHandler : IQueryHandler<LifecycleQuery, LifecycleAnswer>
{
    public Task<LifecycleAnswer> Handle(LifecycleQuery query, CancellationToken cancellationToken)
        => query.Throw ? throw new InvalidOperationException("query exploded") : Task.FromResult(new LifecycleAnswer("42"));
}

[RecordOutcome]
public sealed class CountingStream : StreamRequestBase<int>
{
    public int Count { get; init; }
}

public sealed class CountingStreamHandler : IStreamRequestHandler<CountingStream, int>
{
    public async IAsyncEnumerable<int> Handle(CountingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= request.Count; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}

[ThrowingPostHandler]
public sealed class PostHandlerFailingStream : StreamRequestBase<int>;

public sealed class PostHandlerFailingStreamHandler : IStreamRequestHandler<PostHandlerFailingStream, int>
{
    public async IAsyncEnumerable<int> Handle(PostHandlerFailingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        yield return 2;
    }
}

public sealed class CancellableStream : StreamRequestBase<int>
{
    public bool HandlerTokenCancelled { get; set; }
}

public sealed class CancellableStreamHandler : IStreamRequestHandler<CancellableStream, int>
{
    public async IAsyncEnumerable<int> Handle(CancellableStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        try
        {
            // Only a cancellation ends this wait.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            request.HandlerTokenCancelled = cancellationToken.IsCancellationRequested;
        }

        yield return 2;
    }
}

public sealed class TransactionallyMarkedQuery : QueryBase<LifecycleAnswer>, ITransactionalCommand
{
    public System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.Unspecified;
}

public sealed class TransactionallyMarkedQueryHandler : IQueryHandler<TransactionallyMarkedQuery, LifecycleAnswer>
{
    public Task<LifecycleAnswer> Handle(TransactionallyMarkedQuery query, CancellationToken cancellationToken)
        => Task.FromResult(new LifecycleAnswer("written"));
}

public sealed class CommandMarkedQuery : QueryBase<LifecycleAnswer>, ICommandMarker
{
    public bool Throw { get; init; }
}

public sealed class CommandMarkedQueryHandler : IQueryHandler<CommandMarkedQuery, LifecycleAnswer>
{
    public Task<LifecycleAnswer> Handle(CommandMarkedQuery query, CancellationToken cancellationToken)
        => query.Throw
            ? throw new InvalidOperationException("marked query exploded")
            : Task.FromResult(new LifecycleAnswer("marked"));
}
