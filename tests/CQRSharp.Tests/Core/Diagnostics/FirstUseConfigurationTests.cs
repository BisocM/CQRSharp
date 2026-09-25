using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Modules;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The CQRCONF rules at their point of first use, with startup validation off: the first dispatch of a request whose
///     marker needs a behavior that is not registered (CQRCONF005 fails it, CQRCONF006 logs once), the first publish that
///     would be stored while the transactional outbox has no unit of work (CQRCONF007), and the first publish under the
///     outbox of a handled notification the serializer does not name (CQRCONF003 logs once, CQRCONF010 fails it). Each
///     check runs once per provider and type, and a failure it finds fails every later dispatch too.
/// </summary>
public sealed class FirstUseConfigurationTests
{
    [Fact(DisplayName = "CQRCONF005: an idempotent request without the idempotency behavior fails every dispatch, naming the request, the verb and the code")]
    public async Task Idempotent_request_without_idempotency_fails_every_dispatch()
    {
        var recorder = new FirstUseRecorder();
        await using var provider = Build(b => b.UseLogging(), recorder);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        for (var i = 0; i < 2; i++)
        {
            var act = () => dispatcher.Send(new FirstUseIdempotentCommand(), TestContext.Current.CancellationToken);
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
                .Should().Contain("CQRCONF005").And.Contain(typeof(FirstUseIdempotentCommand).FullName!).And.Contain("UseIdempotency");
        }

        recorder.Handled.Should().Be(0, "a request that would process duplicates must never run");
    }

    [Fact(DisplayName = "CQRCONF005: fails too when the provider proves no behavior is registered at all")]
    public async Task Idempotent_request_fails_when_no_behavior_is_registered()
    {
        var recorder = new FirstUseRecorder();
        await using var provider = Build(b => b.UseValidation(false).UseExceptionHandling(false), recorder);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var act = () => dispatcher.Send(new FirstUseIdempotentCommand(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF005");
        recorder.Handled.Should().Be(0);
    }

    [Fact(DisplayName = "CQRCONF005: a stream request fails when it is enumerated, and its handler never runs")]
    public async Task Idempotent_stream_without_idempotency_fails()
    {
        var recorder = new FirstUseRecorder();
        foreach (var configure in new Action<ICqrsBuilder>[] { b => b.UseLogging(), b => b.UseValidation(false).UseExceptionHandling(false) })
        {
            await using var provider = Build(configure, recorder);
            await using var scope = provider.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            var act = async () =>
            {
                await foreach (var _ in dispatcher.Stream(new FirstUseIdempotentStream(), TestContext.Current.CancellationToken))
                {
                }
            };

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF005");
        }

        recorder.Handled.Should().Be(0);
    }

    [Fact(DisplayName = "CQRCONF005: with UseIdempotency the idempotent request dispatches")]
    public async Task Idempotent_request_with_idempotency_dispatches()
    {
        var recorder = new FirstUseRecorder();
        await using var provider = Build(b => b.UseIdempotency(), recorder);
        await using var scope = provider.CreateAsyncScope();

        (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FirstUseIdempotentCommand(), TestContext.Current.CancellationToken))
            .IsSuccess.Should().BeTrue();
        recorder.Handled.Should().Be(1);
    }

    [Fact(DisplayName = "CQRCONF005: a request that exempts the registered idempotency behavior is an explicit opt-out, not a gap")]
    public async Task Exempted_idempotency_behavior_is_not_a_gap()
    {
        var recorder = new FirstUseRecorder();
        await using var provider = Build(b => b.UseIdempotency(), recorder);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        (await dispatcher.Send(new FirstUseExemptedIdempotentCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        (await dispatcher.Send(new FirstUseExemptedIdempotentCommand(), TestContext.Current.CancellationToken)).IsSuccess
            .Should().BeTrue("the exempted behavior does not run, so the reused key is not rejected");
        recorder.Handled.Should().Be(2);
    }

    [Fact(DisplayName = "CQRCONF005: an exemption does not stand in for a behavior that is not registered")]
    public async Task Exemption_without_the_behavior_still_fails()
    {
        await using var provider = Build(b => b.UseLogging(), new FirstUseRecorder());
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new FirstUseExemptedIdempotentCommand(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF005");
    }

    [Fact(DisplayName = "CQRCONF006: a retryable request without the resilience behavior runs, and the gap is logged once per provider")]
    public async Task Retryable_request_without_resilience_runs_and_warns_once()
    {
        var recorder = new FirstUseRecorder();
        var log = new ConfigurationLog();
        await using var provider = Build(b => b.UseLogging(), recorder, log);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        for (var i = 0; i < 3; i++)
            (await dispatcher.Send(new FirstUseRetryableCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        recorder.Handled.Should().Be(3);
        var warning = log.Logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.EventId.Id.Should().Be(1204);
        warning.Message.Should().Contain("CQRCONF006").And.Contain(nameof(FirstUseRetryableCommand)).And.Contain("UseResilience");
    }

    [Fact(DisplayName = "A correctly wired request pays for nothing but its cached plan: no check for a request without markers, one decided check for one with")]
    public async Task Correctly_wired_requests_keep_only_the_cached_plan()
    {
        var log = new ConfigurationLog();
        await using var provider = Build(b => b.UseIdempotency().UseResilience(_ => { }), new FirstUseRecorder(), log);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var plans = provider.GetRequiredService<RequestPlanCache>();

        await dispatcher.Send(new FirstUsePlainCommand(), TestContext.Current.CancellationToken);
        await dispatcher.Send(new FirstUseRetryableCommand(), TestContext.Current.CancellationToken);

        plans.Get<FirstUsePlainCommand, CommandResult>().MarkerCheck.Should().BeNull("a request without markers has nothing to check");
        var retryable = plans.Get<FirstUseRetryableCommand, CommandResult>();
        retryable.MarkerCheck.Should().NotBeNull();
        retryable.Should().BeSameAs(plans.Get<FirstUseRetryableCommand, CommandResult>(), "the check lives in the plan, built once");
        log.Logger.Entries.Should().BeEmpty();
    }

    [Fact(DisplayName = "CQRCONF007: the transactional outbox without a unit of work fails a publish that would be stored, and delivers any other in-process")]
    public async Task Transactional_outbox_without_unit_of_work_fails_durable_publishes()
    {
        var recorder = new FirstUseRecorder();
        await using var provider = Build(b => b.UseOutbox(o => o.Transactional().UseInMemoryStore()), recorder);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        for (var i = 0; i < 2; i++)
        {
            var act = () => dispatcher.Publish(new FirstUseNamedNotification(), TestContext.Current.CancellationToken);
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
                .Should().Contain("CQRCONF007").And.Contain("IUnitOfWork").And.Contain("UseUnitOfWork");
        }

        await dispatcher.Publish(new FirstUseUnnamedNotification(), TestContext.Current.CancellationToken);
        recorder.Delivered.Should().Be(1, "a notification the serializer does not name was never going to be stored");
    }

    [Fact(DisplayName = "CQRCONF003: a handled notification the serializer does not name is still delivered in-process, and logged once per provider")]
    public async Task Unnamed_handled_notification_is_delivered_and_logged_once()
    {
        var recorder = new FirstUseRecorder();
        var log = new ConfigurationLog();
        await using var provider = Build(b => b.UseOutbox(o => o.UseInMemoryStore()), recorder, log);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        await dispatcher.Publish(new FirstUseUnnamedNotification(), TestContext.Current.CancellationToken);
        await dispatcher.Publish<INotification>(new FirstUseUnnamedNotification(), TestContext.Current.CancellationToken);

        recorder.Delivered.Should().Be(2);
        var warning = log.Logger.Entries.Should().ContainSingle().Subject;
        warning.EventId.Id.Should().Be(1205);
        warning.Message.Should().Contain("CQRCONF003").And.Contain(nameof(FirstUseUnnamedNotification)).And.Contain("[NotificationName]");
    }

    [Fact(DisplayName = "CQRCONF003 is not logged while the outbox is off: nothing bypasses it")]
    public async Task Unnamed_notification_is_not_logged_without_the_outbox()
    {
        var recorder = new FirstUseRecorder();
        var log = new ConfigurationLog();
        await using var provider = Build(b => b.UseLogging(), recorder, log);
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new FirstUseUnnamedNotification(), TestContext.Current.CancellationToken);

        recorder.Delivered.Should().Be(1);
        log.Logger.Entries.Should().BeEmpty();
    }

    [Fact(DisplayName = "CQRCONF011: handlers registered by hand for a durable notification are logged once at its first publish to the outbox, and it is still stored")]
    public async Task Hand_registered_handlers_of_a_durable_notification_are_logged_once()
    {
        var recorder = new FirstUseRecorder();
        var log = new ConfigurationLog();
        await using var provider = Build(b => b.UseOutbox(o => o.UseInMemoryStore()), recorder, log,
            services => services.AddTransient<INotificationHandler<FirstUseNamedNotification>, HandRegisteredNamedHandler>());
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        await dispatcher.Publish(new FirstUseNamedNotification(), TestContext.Current.CancellationToken);
        await dispatcher.Publish(new FirstUseNamedNotification(), TestContext.Current.CancellationToken);

        recorder.Delivered.Should().Be(0, "a publish outside a request is stored straight away, for the processor to deliver");
        var warning = log.Logger.Entries.Should().ContainSingle().Subject;
        warning.EventId.Id.Should().Be(1205);
        warning.Message.Should().Contain("CQRCONF011").And.Contain(typeof(HandRegisteredNamedHandler).FullName!);
    }

    [Fact(DisplayName = "CQRCONF012: handlers registered by hand that cannot be constructed are logged once as an error; the durable publish is not failed")]
    public async Task Unconstructible_hand_registered_handlers_are_logged_once()
    {
        var log = new ConfigurationLog();
        await using var provider = Build(b => b.UseOutbox(o => o.UseInMemoryStore()), new FirstUseRecorder(), log,
            services => services.AddTransient<INotificationHandler<FirstUseNamedNotification>>(_ => throw new InvalidOperationException("boom-ctor")));
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        await dispatcher.Publish(new FirstUseNamedNotification(), TestContext.Current.CancellationToken);
        await dispatcher.Publish(new FirstUseNamedNotification(), TestContext.Current.CancellationToken);

        var error = log.Logger.Entries.Should().ContainSingle().Subject;
        error.Level.Should().Be(LogLevel.Error);
        error.EventId.Id.Should().Be(1206);
        error.Message.Should().Contain("CQRCONF012").And.Contain("boom-ctor");
    }

    [Fact(DisplayName = "CQRCONF010: a handled notification that lost its name to another module's type fails a publish through the outbox")]
    public async Task Notification_that_lost_its_name_fails_its_durable_publish()
    {
        var handled = new List<INotification>();
        var library = new TestModule
        {
            NotificationRoutes = new Dictionary<Type, NotificationRoute> { [typeof(LosingEvent)] = NotificationRoute.For<LosingEvent>() },
            NotificationSubscriptions = [NotificationSubscription.For<LosingEventHandler, LosingEvent>("library.losing-handler")],
            OutboxSerializer = new SingleTypeNotificationSerializer<LosingEvent>("orders.placed")
        };
        var host = new TestModule
        {
            NotificationRoutes = new Dictionary<Type, NotificationRoute> { [typeof(WinningEvent)] = NotificationRoute.For<WinningEvent>() },
            OutboxSerializer = new SingleTypeNotificationSerializer<WinningEvent>("orders.placed")
        };
        var services = TestModule.Compose(new ServiceCollection(), library, host);
        services.AddSingleton(handled);
        services.AddTransient<LosingEventHandler>();
        services.AddInMemoryOutboxStore();
        services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisher = provider.GetRequiredService<NotificationPublisher>();

        for (var i = 0; i < 2; i++)
        {
            var act = () => publisher.Publish(scope.ServiceProvider, new LosingEvent(), TestContext.Current.CancellationToken);
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
                .Should().Contain("CQRCONF010").And.Contain("orders.placed").And.Contain(typeof(LosingEvent).FullName!);
        }

        handled.Should().BeEmpty();
    }

    private static ServiceProvider Build(
        Action<ICqrsBuilder> configure, FirstUseRecorder recorder, ConfigurationLog? log = null, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        if (log is not null) services.AddSingleton<ILoggerProvider>(log);
        services.AddCqrsGenerated(configure);
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>Captures what the first-use checks log, under their own category.</summary>
    private sealed class ConfigurationLog : ILoggerProvider
    {
        public CapturingLogger<ConfigurationLog> Logger { get; } = new();

        public ILogger CreateLogger(string categoryName)
            => categoryName == CqrsConfigurationLog.Category ? Logger : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
        }
    }

    private sealed class HandRegisteredNamedHandler : INotificationHandler<FirstUseNamedNotification>
    {
        public Task Handle(FirstUseNamedNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed record LosingEvent : INotification;

    private sealed record WinningEvent : INotification;

    private sealed class LosingEventHandler(List<INotification> handled) : INotificationHandler<LosingEvent>
    {
        public Task Handle(LosingEvent notification, CancellationToken cancellationToken)
        {
            handled.Add(notification);
            return Task.CompletedTask;
        }
    }
}

/// <summary>Counts what the first-use fixtures' handlers did.</summary>
public sealed class FirstUseRecorder
{
    private int _handled;
    private int _delivered;

    public int Handled => Volatile.Read(ref _handled);
    public int Delivered => Volatile.Read(ref _delivered);

    public void Handle() => Interlocked.Increment(ref _handled);
    public void Deliver() => Interlocked.Increment(ref _delivered);
}

public sealed class FirstUsePlainCommand : CommandBase;

public sealed class FirstUseIdempotentCommand : CommandBase, IIdempotentRequest
{
    public string IdempotencyKey { get; } = Guid.NewGuid().ToString("N");
}

/// <summary>Idempotent, with the idempotency behavior exempted: one key used twice is processed twice.</summary>
[PipelineExemption(typeof(IdempotencyBehavior<,>))]
public sealed class FirstUseExemptedIdempotentCommand : CommandBase, IIdempotentRequest
{
    public string IdempotencyKey => "first-use-exempted";
}

public sealed class FirstUseRetryableCommand : CommandBase, IRetryableRequest;

public sealed class FirstUseIdempotentStream : StreamRequestBase<int>, IIdempotentRequest
{
    public string IdempotencyKey { get; } = Guid.NewGuid().ToString("N");
}

public sealed class FirstUseCommandHandler(FirstUseRecorder recorder) :
    ICommandHandler<FirstUsePlainCommand>,
    ICommandHandler<FirstUseIdempotentCommand>,
    ICommandHandler<FirstUseExemptedIdempotentCommand>,
    ICommandHandler<FirstUseRetryableCommand>,
    IStreamRequestHandler<FirstUseIdempotentStream, int>
{
    public Task<CommandResult> Handle(FirstUsePlainCommand command, CancellationToken cancellationToken) => Handled();
    public Task<CommandResult> Handle(FirstUseIdempotentCommand command, CancellationToken cancellationToken) => Handled();
    public Task<CommandResult> Handle(FirstUseExemptedIdempotentCommand command, CancellationToken cancellationToken) => Handled();
    public Task<CommandResult> Handle(FirstUseRetryableCommand command, CancellationToken cancellationToken) => Handled();

    public async IAsyncEnumerable<int> Handle(FirstUseIdempotentStream request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        recorder.Handle();
        await Task.CompletedTask;
        yield return 1;
    }

    private Task<CommandResult> Handled()
    {
        recorder.Handle();
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

[NotificationName("tests.first-use.named")]
public sealed record FirstUseNamedNotification : INotification;

/// <summary>Handled, and deliberately without a stable name.</summary>
public sealed record FirstUseUnnamedNotification : INotification;

public sealed class FirstUseNotificationHandler(FirstUseRecorder recorder) :
    INotificationHandler<FirstUseNamedNotification>, INotificationHandler<FirstUseUnnamedNotification>
{
    public Task Handle(FirstUseNamedNotification notification, CancellationToken cancellationToken)
    {
        recorder.Deliver();
        return Task.CompletedTask;
    }

    public Task Handle(FirstUseUnnamedNotification notification, CancellationToken cancellationToken)
    {
        recorder.Deliver();
        return Task.CompletedTask;
    }
}
