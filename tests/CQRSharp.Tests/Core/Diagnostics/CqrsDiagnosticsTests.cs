using System.Runtime.CompilerServices;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     <see cref="ICqrsDiagnostics" /> describing requests: the pipeline in execution order (exempted behaviors
///     reported, not run), the pre- and post-handler attributes, the handler that serves a request whichever module
///     routes it, and the binding issues a request carries (CQRDIAG003 for a context without a factory, CQRDIAG004 for
///     a behavior that cannot be constructed).
/// </summary>
public sealed class CqrsDiagnosticsTests
{
    [Fact]
    public void AddCqrsGenerated_registers_ICqrsDiagnostics()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();

        diagnostics.TryDescribeRequest(typeof(TestCommand), out var binding).Should().BeTrue();
        binding!.RequestType.Should().Be(typeof(TestCommand));
        binding.HandlerType.Should().Be(typeof(TestCommandHandler));
        binding.Issues.Should().BeEmpty();
    }

    [Fact]
    public void TryDescribeRequest_returns_false_for_unknown_type()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
        diagnostics.TryDescribeRequest(typeof(string), out _).Should().BeFalse();
    }

    [Fact]
    public void DescribeRequest_sorts_pipeline_behaviors_deterministically()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false).UseExceptionHandling(false));

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(DiagnosticsBehaviorC<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(DiagnosticsBehaviorB<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(DiagnosticsBehaviorA<,>));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
        var binding = diagnostics.DescribeRequest(typeof(DiagnosticsSortedCommand));

        binding.Pipeline.Select(b => b.BehaviorType).Should().Equal(
            typeof(DiagnosticsBehaviorA<DiagnosticsSortedCommand, CommandResult>),
            typeof(DiagnosticsBehaviorB<DiagnosticsSortedCommand, CommandResult>),
            typeof(DiagnosticsBehaviorC<DiagnosticsSortedCommand, CommandResult>));
    }

    [Fact]
    public void DescribeRequest_sorts_stream_pipeline_behaviors_deterministically()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false).UseExceptionHandling(false));

        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(DiagnosticsStreamBehaviorC<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(DiagnosticsStreamBehaviorB<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(DiagnosticsStreamBehaviorA<,>));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
        var binding = diagnostics.DescribeRequest(typeof(DiagnosticsSortedStreamRequest));

        binding.ResponseType.Should().Be(typeof(IAsyncEnumerable<int>));
        binding.Pipeline.Select(b => b.BehaviorType).Should().Equal(
            typeof(DiagnosticsStreamBehaviorA<DiagnosticsSortedStreamRequest, int>),
            typeof(DiagnosticsStreamBehaviorB<DiagnosticsSortedStreamRequest, int>),
            typeof(DiagnosticsStreamBehaviorC<DiagnosticsSortedStreamRequest, int>));
    }

    [Fact]
    public void DescribeRequest_reports_exempted_pipeline_behaviors()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(DiagnosticsBehaviorA<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(DiagnosticsBehaviorB<,>));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
        var binding = diagnostics.DescribeRequest(typeof(DiagnosticsExemptCommand));

        binding.PipelineExemptions.Should().Contain(typeof(DiagnosticsBehaviorB<,>));
        binding.Pipeline.Select(b => b.BehaviorType).Should().Contain(typeof(DiagnosticsBehaviorA<DiagnosticsExemptCommand, CommandResult>));
        binding.Pipeline.Select(b => b.BehaviorType).Should().NotContain(typeof(DiagnosticsBehaviorB<DiagnosticsExemptCommand, CommandResult>));
        binding.ExemptedPipeline.Select(b => b.BehaviorType).Should().Contain(typeof(DiagnosticsBehaviorB<DiagnosticsExemptCommand, CommandResult>));
    }

    [Fact]
    public void DescribeRequest_includes_interceptor_attributes()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
        var binding = diagnostics.DescribeRequest(typeof(DiagnosticsInterceptorCommand));

        binding.PreHandlers.Should().ContainSingle(h =>
            h.AttributeType == typeof(DiagnosticsInterceptorAttribute) &&
            h.Priority == 123);

        binding.PostHandlers.Should().ContainSingle(h =>
            h.AttributeType == typeof(DiagnosticsInterceptorAttribute) &&
            h.Priority == 123);
    }

    [Fact(DisplayName = "CQRDIAG005: a request with validators but no validation behavior is described with a warning; the default registration has none")]
    public void Validators_without_the_validation_behavior_are_reported()
    {
        static IReadOnlyList<CqrsBindingIssue> Issues(Action<ICqrsBuilder> configure)
        {
            var services = new ServiceCollection();
            services.AddCqrsGenerated(configure);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeRequest(typeof(DiscoveredValidatedCommand)).Issues;
        }

        Issues(b => b.UseValidation(false)).Should().ContainSingle(i => i.Code == "CQRDIAG005")
            .Which.Severity.Should().Be(CqrsBindingIssueSeverity.Warning);
        Issues(_ => { }).Should().NotContain(i => i.Code == "CQRDIAG005");
        Issues(b => b.UseValidation(false)).Should().NotContain(i => i.Code == "CQRDIAG006", "the request has no exception hooks");
    }

    [Fact(DisplayName = "CQRDIAG006: a request with exception hooks but no exception-handling behavior is described with a warning; the default registration has none")]
    public void Exception_hooks_without_the_exception_handling_behavior_are_reported()
    {
        static IReadOnlyList<CqrsBindingIssue> Issues(Action<ICqrsBuilder> configure)
        {
            var services = new ServiceCollection();
            services.AddCqrsGenerated(configure);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeRequest(typeof(HandledExceptionCommand)).Issues;
        }

        Issues(b => b.UseExceptionHandling(false)).Should().ContainSingle(i => i.Code == "CQRDIAG006")
            .Which.Severity.Should().Be(CqrsBindingIssueSeverity.Warning);
        Issues(_ => { }).Should().NotContain(i => i.Code == "CQRDIAG006");
    }

    [Fact(DisplayName = "A request whose context type has no factory is described with CQRDIAG003")]
    public async Task Missing_context_factory_is_CQRDIAG003()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        // The generator registers the factory it found in this assembly; take it away so the binding has none.
        services.RemoveAll<IRequestContextFactory<DiagnosticsCustomContext>>();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var binding = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeRequest(typeof(DiagnosticsCustomContextCommand));

        binding.ContextType.Should().Be(typeof(DiagnosticsCustomContext));
        binding.Issues.Should().ContainSingle().Which.Code.Should().Be("CQRDIAG003");
    }

    [Fact(DisplayName = "A behavior that cannot be constructed is CQRDIAG004, and no marker check concludes a behavior is missing")]
    public async Task Unresolvable_behaviors_are_CQRDIAG004_not_a_missing_behavior()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseInMemoryStore()).UseResilience(_ => { }));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(DependentBehavior<,>));
        services.AddTransient<UnavailableDependency>(_ => throw new InvalidOperationException("the dependency is down"));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();

        diagnostics.DescribeRequest(typeof(IdempotentTestCommand)).Issues
            .Should().ContainSingle().Which.Code.Should().Be("CQRDIAG004");
        diagnostics.DescribeConfiguration().Should().NotContain(i => i.Code == "CQRCONF005" || i.Code == "CQRCONF006",
            "the pipeline is unknown, not empty: the idempotency and resilience behaviors are registered");
    }

    [Fact(DisplayName = "A closed generic request the assembly routes is described, with its handler")]
    public async Task Closed_generic_request_is_described()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();

        var binding = diagnostics.DescribeRequest(typeof(EchoQuery<int>));

        binding.HandlerType.Should().Be(typeof(EchoIntQueryHandler));
        binding.ResponseType.Should().Be(typeof(int));
        binding.Issues.Should().BeEmpty();
        diagnostics.DescribeAllRequests().Select(b => b.RequestType).Should().Contain(typeof(EchoQuery<int>));
    }

    [Fact(DisplayName = "A request two modules handle is described once, with the handler that serves it")]
    public async Task Request_routed_by_two_modules_is_described_once()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var bindings = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeAllRequests();

        bindings.Where(b => b.RequestType == typeof(ExternalModule.SharedCommand))
            .Should().ContainSingle().Which.HandlerType.Should().Be(typeof(SharedCommandHandler));
    }

    [Fact(DisplayName = "DescribeAllRequests covers the requests of every module, ordered by type name")]
    public async Task Every_module_is_described_in_name_order()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var types = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeAllRequests().Select(b => b.RequestType).ToArray();

        types.Should().Contain([typeof(TestCommand), typeof(ExternalModule.ExternalQuery), typeof(DiagnosticsSortedStreamRequest)]);
        types.Select(t => t.FullName).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact(DisplayName = "DescribeRequest names a type no module routes")]
    public async Task Unknown_request_type_throws()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeRequest(typeof(string));

        act.Should().Throw<InvalidOperationException>().WithMessage("*System.String*");
    }
}

[DiagnosticsInterceptor(123)]
internal sealed class DiagnosticsInterceptorCommand : CommandBase;

[PipelineExemption(typeof(DiagnosticsBehaviorB<,>))]
internal sealed class DiagnosticsExemptCommand : CommandBase;

internal sealed class DiagnosticsSortedCommand : CommandBase;

internal sealed class DiagnosticsInterceptorCommandHandler : ICommandHandler<DiagnosticsInterceptorCommand>
{
    public Task<CommandResult> Handle(DiagnosticsInterceptorCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

internal sealed class DiagnosticsExemptCommandHandler : ICommandHandler<DiagnosticsExemptCommand>
{
    public Task<CommandResult> Handle(DiagnosticsExemptCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

internal sealed class DiagnosticsSortedCommandHandler : ICommandHandler<DiagnosticsSortedCommand>
{
    public Task<CommandResult> Handle(DiagnosticsSortedCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

internal sealed class DiagnosticsBehaviorA<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public Task<TResult> Handle(
        TRequest request,
        RequestHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);

    public int PipelineExecutionPriority => 10;
}

internal sealed class DiagnosticsBehaviorB<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public Task<TResult> Handle(
        TRequest request,
        RequestHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);

    public int PipelineExecutionPriority => 10;
}

internal sealed class DiagnosticsBehaviorC<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public Task<TResult> Handle(
        TRequest request,
        RequestHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);

    public int PipelineExecutionPriority => 20;
}

internal sealed class UnavailableDependency;

internal sealed class DependentBehavior<TRequest, TResult>(UnavailableDependency dependency) : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
        => dependency is null ? throw new InvalidOperationException() : next(cancellationToken);
}

internal sealed class DiagnosticsCustomContext : RequestContextBase;

internal sealed class DiagnosticsCustomContextFactory : IRequestContextFactory<DiagnosticsCustomContext>
{
    public ValueTask<DiagnosticsCustomContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new DiagnosticsCustomContext());
}

internal sealed class DiagnosticsCustomContextCommand : CommandBase<DiagnosticsCustomContext>;

internal sealed class DiagnosticsSortedStreamRequest : StreamRequestBase<int>;

internal sealed class DiagnosticsSortedStreamRequestHandler : IStreamRequestHandler<DiagnosticsSortedStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(DiagnosticsSortedStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
    }
}

internal sealed class DiagnosticsStreamBehaviorA<TRequest, TItem> : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => 10;

    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}

internal sealed class DiagnosticsStreamBehaviorB<TRequest, TItem> : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => 10;

    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}

internal sealed class DiagnosticsStreamBehaviorC<TRequest, TItem> : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => 20;

    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}

internal sealed class DiagnosticsCustomContextCommandHandler : ICommandHandler<DiagnosticsCustomContextCommand>
{
    public Task<CommandResult> Handle(DiagnosticsCustomContextCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class DiagnosticsInterceptorAttribute(int priority) : Attribute, IPreHandlerAttribute, IPostHandlerAttribute
{
    public int PreHandlerExecutionPriority => priority;
    public int PostHandlerExecutionPriority => priority;

    public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

