using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Diagnostics.HealthChecks;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CQRSharp.Tests.Core;

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
        binding.RequestType.Should().Be(typeof(TestCommand));
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
        services.AddCqrsGenerated();

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
        services.AddCqrsGenerated();

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

    [Fact]
    public async Task CqrsBindingsHealthCheck_is_healthy_when_all_bindings_valid()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        services.AddTransient<IRequestContextFactory<DiagnosticsCustomContext>, DiagnosticsCustomContextFactory>();
        services.AddHealthChecks().AddCqrsBindings();

        using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<HealthCheckService>();

        var report = await health.CheckHealthAsync();
        report.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CqrsBindingsHealthCheck_is_unhealthy_when_context_factory_missing()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        services.AddHealthChecks().AddCqrsBindings();

        using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<HealthCheckService>();

        var report = await health.CheckHealthAsync();
        report.Status.Should().Be(HealthStatus.Unhealthy);

        report.Entries.Should().ContainKey("cqrsharp.bindings");
        report.Entries["cqrsharp.bindings"].Data.Should().ContainKey("errorCount");
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
    public int PipelineExecutionPriority => 10;

    public Task<TResult> Handle(
        TRequest request,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}

internal sealed class DiagnosticsBehaviorB<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => 10;

    public Task<TResult> Handle(
        TRequest request,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}

internal sealed class DiagnosticsBehaviorC<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => 20;

    public Task<TResult> Handle(
        TRequest request,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}

internal sealed class DiagnosticsCustomContext : RequestContextBase;

internal sealed class DiagnosticsCustomContextFactory : IRequestContextFactory<DiagnosticsCustomContext>
{
    public DiagnosticsCustomContext CreateContext(IRequest request) => new();
}

internal sealed class DiagnosticsCustomContextCommand : CommandBase<DiagnosticsCustomContext>;

internal sealed class DiagnosticsSortedStreamRequest : StreamRequestBase<int>;

internal sealed class DiagnosticsSortedStreamRequestHandler : IStreamRequestHandler<DiagnosticsSortedStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(DiagnosticsSortedStreamRequest request, CancellationToken cancellationToken)
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
        Func<CancellationToken, IAsyncEnumerable<TItem>> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}

internal sealed class DiagnosticsStreamBehaviorB<TRequest, TItem> : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => 10;

    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        Func<CancellationToken, IAsyncEnumerable<TItem>> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}

internal sealed class DiagnosticsStreamBehaviorC<TRequest, TItem> : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => 20;

    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        Func<CancellationToken, IAsyncEnumerable<TItem>> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}

internal sealed class DiagnosticsCustomContextCommandHandler : ICommandHandler<DiagnosticsCustomContextCommand>
{
    public Task<CommandResult> Handle(DiagnosticsCustomContextCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class DiagnosticsInterceptorAttribute(int priority) : Attribute, ICommandInterceptor
{
    public int PreHandlerExecutionPriority => priority;
    public int PostHandlerExecutionPriority => priority;

    public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task OnAfterHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
