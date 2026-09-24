using CQRSharp.Core.Notifications;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The executor's own rules, through the dispatch path applications run: pre- and post-handlers in priority order
///     (equal priorities by type name, whatever order they are declared in), post-handlers that receive the request's
///     outcome — the typed result it returned or the exception it threw — every post-handler running whatever the others
///     do, and behavior exemptions that never disturb the container's cached behavior arrays.
/// </summary>
public sealed class PipelineExecutorTests
{
    [Fact(DisplayName = "Pre- and post-handlers run by priority, equal priorities by type name, whatever order they are declared in")]
    public async Task Interceptors_run_in_priority_order()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<InterceptorProbe>();

        (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new OrderedInterceptorsCommand(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        probe.Calls.Should().Equal(
            "pre:first", "pre:alpha", "pre:zulu",
            "handle",
            "post:first", "post:alpha", "post:zulu");
    }

    [Fact(DisplayName = "A post-handler receives the typed result the handler returned, so it can act on a verdict a successful request carried")]
    public async Task Post_handler_receives_the_returned_result()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<InterceptorProbe>();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new VerdictCommand { Verdict = "denied" }, TestContext.Current.CancellationToken);

        var (_, outcome) = probe.Outcomes.Should().ContainSingle().Subject;
        outcome.Threw.Should().BeFalse();
        outcome.Result.Should().BeSameAs(result);
        result.Value.Should().Be("denied");
    }

    [Fact(DisplayName = "A post-handler that throws after the handler failed stops no other post-handler, and the caller receives the handler's exception")]
    public async Task Failing_post_handler_on_the_failure_path_stops_no_other()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = Build(recorder.Subscribe);
        await using var scope = provider.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<InterceptorProbe>();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new AuditedCommand { HandlerThrows = true });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("handler failed");
        probe.Outcomes.Select(o => o.Name).Should().Equal("metrics", "cache", "audit");
        probe.Outcomes.Should().OnlyContain(o => o.Outcome.Exception!.Message == "handler failed");
        recorder.Published.OfType<CommandFailedNotification>().Should().ContainSingle()
            .Which.Exception.Message.Should().Be("handler failed");
    }

    [Fact(DisplayName = "A post-handler that throws after the handler succeeded fails the request, and the post-handlers after it observe that failure")]
    public async Task Failing_post_handler_on_the_success_path_fails_the_request()
    {
        var recorder = new LifecycleRecorder();
        await using var provider = Build(recorder.Subscribe);
        await using var scope = provider.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<InterceptorProbe>();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new AuditedCommand());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("cache invalidation failed");
        probe.Outcomes.Select(o => o.Name).Should().Equal("metrics", "cache", "audit");
        probe.Outcomes[0].Outcome.Threw.Should().BeFalse("the post-handlers before the failing one observed the success");
        probe.Outcomes[2].Outcome.Exception!.Message.Should().Be("cache invalidation failed");
        recorder.Published.OfType<CommandFailedNotification>().Should().ContainSingle()
            .Which.Exception.Message.Should().Be("cache invalidation failed");
        recorder.Published.OfType<CommandCompletedNotification>().Should().BeEmpty();
    }

    [Fact(DisplayName = "Singleton behaviors with an exemption run exactly once per dispatch, every dispatch")]
    public async Task Singleton_behaviors_are_not_corrupted_by_the_exemption_filter()
    {
        var counter = new BehaviorCounter();
        var services = new ServiceCollection();
        services.AddSingleton(counter);
        services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(CountingBehaviorA<,>));
        services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(SkippedBehavior<,>));
        services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(CountingBehaviorC<,>));
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        for (var i = 0; i < 3; i++) await dispatcher.Send(new ExemptingQuery(), TestContext.Current.CancellationToken);

        counter.A.Should().Be(3);
        counter.C.Should().Be(3);
        counter.Skipped.Should().Be(0);
    }

    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddScoped<InterceptorProbe>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

/// <summary>Per-scope record of what ran; only the tests that register it record anything.</summary>
public sealed class InterceptorProbe
{
    public List<string> Calls { get; } = [];

    public List<(string Name, RequestOutcome Outcome)> Outcomes { get; } = [];
}

/// <summary>A post-handler that records the outcome it observed, and throws when told to.</summary>
public abstract class OutcomeRecordingPostHandlerAttribute(string name, int priority, bool throws) : Attribute, IPostHandlerAttribute
{
    public int PostHandlerExecutionPriority => priority;

    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        serviceProvider.GetService<InterceptorProbe>()?.Outcomes.Add((name, outcome));
        return throws ? throw new InvalidOperationException("cache invalidation failed") : Task.CompletedTask;
    }
}

public sealed class MetricsPostHandlerAttribute() : OutcomeRecordingPostHandlerAttribute("metrics", -1, false);

public sealed class CacheInvalidationPostHandlerAttribute() : OutcomeRecordingPostHandlerAttribute("cache", 0, true);

public sealed class AuditPostHandlerAttribute() : OutcomeRecordingPostHandlerAttribute("audit", 1, false);

[MetricsPostHandler]
[CacheInvalidationPostHandler]
[AuditPostHandler]
public sealed class AuditedCommand : CommandBase
{
    public bool HandlerThrows { get; init; }
}

public sealed class AuditedCommandHandler : ICommandHandler<AuditedCommand>
{
    public Task<CommandResult> Handle(AuditedCommand command, CancellationToken cancellationToken)
        => command.HandlerThrows
            ? throw new InvalidOperationException("handler failed")
            : Task.FromResult(CommandResult.FromSuccess());
}

/// <summary>A command whose successful result carries a verdict for its post-handler to read.</summary>
[MetricsPostHandler]
public sealed class VerdictCommand : ResultCommandBase<string>
{
    public required string Verdict { get; init; }
}

public sealed class VerdictCommandHandler : IResultCommandHandler<VerdictCommand, string>
{
    public Task<CommandResult<string>> Handle(VerdictCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult<string>.FromSuccess(command.Verdict));
}

/// <summary>A pre- and post-handler that records its name; the subclasses differ only in type name and priority.</summary>
public abstract class RecordingInterceptorAttribute(string name, int priority) : Attribute, IPreHandlerAttribute, IPostHandlerAttribute
{
    public int PreHandlerExecutionPriority => priority;

    public int PostHandlerExecutionPriority => priority;

    public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        serviceProvider.GetService<InterceptorProbe>()?.Calls.Add("pre:" + name);
        return Task.CompletedTask;
    }

    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        serviceProvider.GetService<InterceptorProbe>()?.Calls.Add("post:" + name);
        return Task.CompletedTask;
    }
}

public sealed class ZuluInterceptorAttribute() : RecordingInterceptorAttribute("zulu", 5);

public sealed class AlphaInterceptorAttribute() : RecordingInterceptorAttribute("alpha", 5);

public sealed class FirstInterceptorAttribute() : RecordingInterceptorAttribute("first", -10);

// Declared in the reverse of the order they run in: the sort, not the declaration, decides.
[ZuluInterceptor]
[AlphaInterceptor]
[FirstInterceptor]
public sealed class OrderedInterceptorsCommand : CommandBase;

public sealed class OrderedInterceptorsCommandHandler(IServiceProvider services) : ICommandHandler<OrderedInterceptorsCommand>
{
    public Task<CommandResult> Handle(OrderedInterceptorsCommand command, CancellationToken cancellationToken)
    {
        services.GetService<InterceptorProbe>()?.Calls.Add("handle");
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

public sealed class BehaviorCounter
{
    public int A;
    public int C;
    public int Skipped;
}

public sealed class CountingBehaviorA<TRequest, TResult>(BehaviorCounter counter) : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        if (request is ExemptingQuery) Interlocked.Increment(ref counter.A);
        return next(cancellationToken);
    }
}

public sealed class SkippedBehavior<TRequest, TResult>(BehaviorCounter counter) : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        if (request is ExemptingQuery) Interlocked.Increment(ref counter.Skipped);
        return next(cancellationToken);
    }
}

public sealed class CountingBehaviorC<TRequest, TResult>(BehaviorCounter counter) : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        if (request is ExemptingQuery) Interlocked.Increment(ref counter.C);
        return next(cancellationToken);
    }
}

[PipelineExemption(typeof(SkippedBehavior<,>))]
public sealed class ExemptingQuery : QueryBase<string>;

public sealed class ExemptingQueryHandler : IQueryHandler<ExemptingQuery, string>
{
    public Task<string> Handle(ExemptingQuery query, CancellationToken cancellationToken) => Task.FromResult("ok");
}
