using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Gen = CQRSharp.Generators.CqrsSourceGenerator.CqrsSourceGenerator;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     The generator's incrementality contract. Each case runs the generator twice over a source that fills every model
///     collection (closed behaviors of a request and of a value-type notification, request attributes, exception hooks,
///     validators, context factories, an outbox notification with nested objects and a partition key, an idempotent
///     request's fingerprint) and asserts, step by
///     named step, what the second run recomputed. Code generation re-runs only when a type's CQRSharp shape changes;
///     an edit that only moves code re-runs the (cheap) diagnostics, which then sit where the code now is.
/// </summary>
public sealed class IncrementalGeneratorCachingTests
{
    private const string GenerationInput = "CqrsGenerationInput";
    private const string DiagnosticsInput = "CqrsDiagnosticsInput";
    private const string BootstrapCallSites = "CqrsBootstrapCallSites";

    private const string Types = @"__TOP__using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;
using CQRSharp.Pipelines;

namespace CachingProbe;

public sealed class TraceAttribute(string area) : Attribute, IPreHandlerAttribute
{
    public string Area { get; } = area;
    public int Weight { get; set; }
    public int PreHandlerExecutionPriority => 0;
    public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken) => Task.CompletedTask;
}

[Trace(""billing"", Weight = 3)]
public sealed class ProbeCommand : CommandBase;

public sealed class ProbeCommandHandler : ICommandHandler<ProbeCommand>
{
    public Task<CommandResult> Handle(ProbeCommand command, CancellationToken cancellationToken)
    {
        var marker = __VALUE__;
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

public sealed class ProbeValidator : IRequestValidator<ProbeCommand>
{
    public Task<ValidationFailure[]> ValidateAsync(ProbeCommand request, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<ValidationFailure>());
}

public sealed class ProbeFailed : IRequestExceptionAction<ProbeCommand, InvalidOperationException>
{
    public Task Execute(ProbeCommand request, InvalidOperationException exception, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ProbeRecovered : IRequestExceptionHandler<ProbeCommand, CommandResult, InvalidOperationException>
{
    public Task Handle(ProbeCommand request, InvalidOperationException exception, RequestExceptionHandlerState<CommandResult> state, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CountQuery : QueryBase<int>;

public sealed class CountQueryHandler : IQueryHandler<CountQuery, int>
{
    public Task<int> Handle(CountQuery query, CancellationToken cancellationToken) => Task.FromResult(1);
}

public sealed class Audit<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
}

public sealed record Address(string Street, string City);

[NotificationName(""probe.placed"", PartitionBy = nameof(OrderId))]
public sealed record OrderPlaced(Guid OrderId, Address Ship, List<string> Tags) : INotification;

[NotificationHandlerName(""probe.placed.handler"")]
public sealed class OnOrderPlaced : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public readonly struct Tick : INotification;

public sealed class OnTick : INotificationHandler<Tick>
{
    public Task Handle(Tick notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class Stamp<TNotification> : INotificationPipelineBehavior<TNotification> where TNotification : INotification
{
    public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken) => next(cancellationToken);
}

public sealed class Charge : CommandBase, IIdempotentRequest
{
    public string IdempotencyKey { get; init; } = """";
    public Dictionary<string, decimal> Lines { get; init; } = new();
}

public sealed class ChargeHandler : ICommandHandler<Charge>
{
    public Task<CommandResult> Handle(Charge command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}

public sealed class TenantContext : RequestContextBase;

public sealed class TenantContextFactory : IRequestContextFactory<TenantContext>
{
    public ValueTask<TenantContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new TenantContext());
}

public sealed class Orphan : CommandBase;
";

    private const string Wiring = @"using Microsoft.Extensions.DependencyInjection;
using CQRSharp;

namespace CachingProbe;

public static class Wiring
{__PADDING__
    public static void Wire(IServiceCollection services) => services.AddCqrsGenerated();
}
";

    private const string Unrelated = @"namespace CachingProbe;

public sealed class Unrelated
{
    public int Value => __UNRELATED__;
}
";

    [Fact(DisplayName = "The probe exercises every generated file and every model collection")]
    public void Probe_fills_every_model()
    {
        var (driver, compilation) = FirstRun();
        var sources = driver.GetRunResult().Results.Single().GeneratedSources.ToDictionary(s => s.HintName, s => s.SourceText.ToString());

        sources.Keys.Should().Contain([
            "CqrsModule.g.cs", "GeneratedOutboxNotificationSerializer.g.cs", "GeneratedRequestFingerprinter.g.cs",
            "CqrsGeneratedBootstrap.g.cs", "CqrsGeneratedAssemblyMarkers.g.cs"
        ]);
        var module = sources["CqrsModule.g.cs"];
        module.Should().Contain("CreateInstance<global::CachingProbe.Audit<global::CachingProbe.CountQuery, int>>")
            .And.Contain(@"new global::CachingProbe.TraceAttribute(""billing"") { Weight = 3 }")
            .And.Contain("RequestExceptionHook.For<global::CachingProbe.ProbeCommand, global::CQRSharp.CommandResult, global::System.InvalidOperationException>(actions: true, handlers: true)")
            .And.Contain("IRequestValidator<global::CachingProbe.ProbeCommand>")
            .And.Contain("DiscoveredContextFactories.Register<global::CachingProbe.TenantContext, global::CachingProbe.TenantContextFactory>(services)")
            .And.Contain("CreateInstance<global::CachingProbe.Stamp<global::CachingProbe.Tick>>")
            .And.Contain(@"NotificationSubscription.For<global::CachingProbe.OnOrderPlaced, global::CachingProbe.OrderPlaced>(""probe.placed.handler"")");
        compilation.GetDiagnostics(TestContext.Current.CancellationToken).Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "A handler body edit serves every step after the transform from cache")]
    public void Body_edit_is_served_from_cache()
    {
        var (driver, _) = FirstRun();
        var before = GeneratedSources(driver);

        var second = driver.RunGenerators(Compile(value: "2"), TestContext.Current.CancellationToken).GetRunResult();

        Healthy(second);
        second.Results.Single().GeneratedSources.Select(s => s.SourceText.ToString()).Should().Equal(before);
        Reasons(second, GenerationInput).Should().OnlyContain(r => IsReused(r));
        Reasons(second, DiagnosticsInput).Should().OnlyContain(r => IsReused(r));
        OutputReasons(second).Should().NotBeEmpty().And.OnlyContain(r => IsReused(r), "a body edit must not re-run generation or diagnostics");
    }

    [Fact(DisplayName = "A line added above the CQRSharp types re-runs only the diagnostics, which move with the code")]
    public void Line_shift_reruns_only_diagnostics()
    {
        var (driver, _) = FirstRun();
        var orphanBefore = OrphanLine(driver.GetRunResult());

        var second = driver.RunGenerators(Compile(top: "// a line added above everything\n"), TestContext.Current.CancellationToken).GetRunResult();

        Healthy(second);
        Reasons(second, GenerationInput).Should().OnlyContain(r => IsReused(r), "only source locations changed, and code generation does not read them");
        OutputReasons(second, GenerationInput).Should().OnlyContain(r => IsReused(r));
        Reasons(second, DiagnosticsInput).Should().Contain(IncrementalStepRunReason.Modified);
        OrphanLine(second).Should().Be(orphanBefore + 1, "a diagnostic is reported where the code now is");
    }

    [Fact(DisplayName = "Moving the AddCqrsGenerated call re-runs only the diagnostics")]
    public void Moving_the_bootstrap_call_reruns_only_diagnostics()
    {
        var (driver, _) = FirstRun();

        var second = driver.RunGenerators(Compile(padding: "\n    // the call moved down a line\n"), TestContext.Current.CancellationToken).GetRunResult();

        Healthy(second);
        Reasons(second, BootstrapCallSites).Should().Contain(IncrementalStepRunReason.Modified);
        Reasons(second, GenerationInput).Should().OnlyContain(r => IsReused(r));
        OutputReasons(second, GenerationInput).Should().OnlyContain(r => IsReused(r));
    }

    [Fact(DisplayName = "An edit in a file with no CQRSharp types serves everything from cache")]
    public void Unrelated_edit_is_served_from_cache()
    {
        var (driver, _) = FirstRun();

        var second = driver.RunGenerators(Compile(unrelated: "2"), TestContext.Current.CancellationToken).GetRunResult();

        Healthy(second);
        OutputReasons(second).Should().NotBeEmpty().And.OnlyContain(r => IsReused(r));
    }

    [Fact(DisplayName = "Adding a request re-runs generation: the control that proves the cases above detect caching")]
    public void Adding_a_request_invalidates()
    {
        var (driver, _) = FirstRun();

        var second = driver.RunGenerators(Compile(extra: "public sealed class ExtraCommand : CommandBase;\n"), TestContext.Current.CancellationToken).GetRunResult();

        Healthy(second);
        Reasons(second, GenerationInput).Should().Contain(IncrementalStepRunReason.Modified);
        OutputReasons(second, GenerationInput).Should().Contain(r => r == IncrementalStepRunReason.Modified || r == IncrementalStepRunReason.New);
    }

    private static (GeneratorDriver Driver, Compilation Output) FirstRun()
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new Gen().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGeneratorsAndUpdateCompilation(Compile(), out var output, out _, TestContext.Current.CancellationToken);
        Healthy(driver.GetRunResult().Results.Single());
        CompilationHarness.Errors(output).Should().BeEmpty("the probe must compile once the generated code is added");
        return (driver, output);
    }

    private static CSharpCompilation Compile(string value = "1", string top = "", string padding = "", string unrelated = "1", string extra = "")
        => CompilationHarness.CreateCompilation(
            [
                Types.Replace("__TOP__", top).Replace("__VALUE__", value) + extra,
                Wiring.Replace("__PADDING__", padding),
                Unrelated.Replace("__UNRELATED__", unrelated)
            ],
            "CachingProbeAssembly");

    private static void Healthy(GeneratorRunResult run)
    {
        run.Exception.Should().BeNull();
        run.Diagnostics.Should().NotContain(d => d.Id == "CQRGEN999");
    }

    private static void Healthy(GeneratorDriverRunResult run) => Healthy(run.Results.Single());

    private static bool IsReused(IncrementalStepRunReason reason)
        => reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged;

    private static IncrementalStepRunReason[] Reasons(GeneratorDriverRunResult run, string step)
        => run.Results.Single().TrackedSteps[step].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();

    // The reasons of the source outputs; with a step name, of the one output that step feeds.
    private static IncrementalStepRunReason[] OutputReasons(GeneratorDriverRunResult run, string? input = null)
        => run.Results.Single().TrackedOutputSteps
            .SelectMany(kvp => kvp.Value)
            .Where(step => input is null || step.Inputs.Any(i => i.Source.Name == input))
            .SelectMany(step => step.Outputs)
            .Select(o => o.Reason)
            .ToArray();

    private static int OrphanLine(GeneratorDriverRunResult run)
        => run.Diagnostics.Single(d => d.Id == "CQRGEN003" && d.GetMessage().Contains("Orphan")).Location.GetLineSpan().StartLinePosition.Line;

    private static string[] GeneratedSources(GeneratorDriver driver)
        => driver.GetRunResult().Results.Single().GeneratedSources.Select(s => s.SourceText.ToString()).ToArray();
}
