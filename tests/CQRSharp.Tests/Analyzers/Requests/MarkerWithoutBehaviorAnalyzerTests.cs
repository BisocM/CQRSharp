using System.Collections.Immutable;
using CQRSharp.Analyzers;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     CQRA018 / CQRA019: an application handles an idempotent (retryable) request, and its CQRSharp configuration, all of
///     it in view, never calls UseIdempotency (UseResilience). Each case compiles an application with the generators, as
///     a build does. The negatives are the point: every shape of configuration the analyzer cannot prove complete must
///     report nothing.
/// </summary>
public sealed class MarkerWithoutBehaviorAnalyzerTests
{
    internal const string Requests = """
                                     using System;
                                     using System.Threading;
                                     using System.Threading.Tasks;
                                     using CQRSharp;
                                     using CQRSharp.Pipelines;
                                     using Microsoft.Extensions.DependencyInjection;

                                     public sealed class PlaceOrder : CommandBase, IIdempotentRequest
                                     {
                                         public string IdempotencyKey => "key";
                                     }
                                     public sealed class Charge : CommandBase, IRetryableRequest;
                                     public sealed class Ping : CommandBase;
                                     public sealed class OrderHandlers : ICommandHandler<PlaceOrder>, ICommandHandler<Charge>, ICommandHandler<Ping>
                                     {
                                         public Task<CommandResult> Handle(PlaceOrder command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
                                         public Task<CommandResult> Handle(Charge command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
                                         public Task<CommandResult> Handle(Ping command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
                                     }
                                     public sealed class AppUnitOfWork(IServiceProvider services) : CQRSharp.Persistence.IUnitOfWork
                                     {
                                         public IServiceProvider Services { get; } = services;
                                         public bool HasActiveTransaction => false;
                                         public Task BeginTransactionAsync(System.Data.IsolationLevel isolationLevel, CancellationToken cancellationToken) => Task.CompletedTask;
                                         public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                                         public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                                     }
                                     public sealed class AppBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
                                     {
                                         public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
                                     }
                                     public static class AppBuilderExtensions
                                     {
                                         public static ICqrsBuilder UseAppDefaults(this ICqrsBuilder builder) => builder.UseIdempotency();
                                         public static void Configure(ICqrsBuilder builder) => builder.UseIdempotency();
                                         public static void Tune(TimeoutOptions options) => options.Timeout = TimeSpan.FromSeconds(1);
                                     }

                                     """;

    [Theory(DisplayName = "CQRA018 and CQRA019: a configuration in view without the verbs reports both, at the registration")]
    [InlineData("services.AddCqrsGenerated()")]
    [InlineData("services.AddCqrsGenerated(b => { })")]
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging())")]
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging().UseOutbox(o => o.Transactional().UseInMemoryStore()).ValidateOnStart())")]
    [InlineData("services.AddCqrsGenerated(b => { b.UseLogging(); b.UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(2)); })")]
    [InlineData("services.AddCqrsGenerated(b => b.UseUnitOfWork(sp => new AppUnitOfWork(sp)).UseTimeProvider(sp => TimeProvider.System))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseFluentValidation())")]
    public async Task Configuration_without_the_verbs_is_flagged(string registration)
    {
        var diagnostics = await AnalyzeAsync(Application(registration));

        var idempotency = diagnostics.Should().ContainSingle(d => d.Id == "CQRA018").Subject;
        idempotency.Severity.Should().Be(DiagnosticSeverity.Error);
        idempotency.GetMessage().Should().Contain("'PlaceOrder'").And.Contain("UseIdempotency").And.Contain("CQRCONF005").And.NotContain("Charge");
        idempotency.Location.SourceTree!.GetText(TestContext.Current.CancellationToken).ToString(idempotency.Location.SourceSpan).Should().Be("AddCqrsGenerated");

        var resilience = diagnostics.Should().ContainSingle(d => d.Id == "CQRA019").Subject;
        resilience.Severity.Should().Be(DiagnosticSeverity.Warning);
        resilience.GetMessage().Should().Contain("'Charge'").And.Contain("UseResilience").And.Contain("CQRCONF006");
    }

    [Theory(DisplayName = "CQRA018 and CQRA019: only the verb that is missing is reported")]
    [InlineData("services.AddCqrsGenerated(b => b.UseIdempotency())", "CQRA019")]
    [InlineData("services.AddCqrsGenerated(b => b.UseResilience(o => o.MaxRetries = 2))", "CQRA018")]
    [InlineData("services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseInMemoryStore()).UseResilience(_ => { }))", null)]
    [InlineData("services.AddCqrsGenerated(b => { b.UseResilience(_ => { }); b.UseIdempotency(); })", null)]
    public async Task Only_the_missing_verb_is_reported(string registration, string? expected)
    {
        var reported = (await AnalyzeAsync(Application(registration)))
            .Where(d => d.Id is "CQRA018" or "CQRA019").Select(d => d.Id);

        reported.Should().Equal(expected is null ? [] : [expected]);
    }

    [Fact(DisplayName = "CQRA018: the application's own behavior, registered with typeof in a plain container registration, keeps the configuration in view")]
    public async Task Own_open_behavior_registration_stays_in_view()
    {
        var diagnostics = await AnalyzeAsync(Application(
            "services.AddCqrsGenerated(b => b.UseLogging()); services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AppBehavior<,>))"));

        diagnostics.Should().Contain(d => d.Id == "CQRA018");
    }

    [Fact(DisplayName = "CQRA018: an exemption does not stand in for a behavior that is not registered")]
    public async Task Exemption_does_not_hide_the_missing_behavior()
    {
        var diagnostics = await AnalyzeAsync(Requests + """
            [PipelineExemption(typeof(IdempotencyBehavior<,>))]
            public sealed class Refund : CommandBase, IIdempotentRequest
            {
                public string IdempotencyKey => "refund";
            }
            public sealed class RefundHandler : ICommandHandler<Refund>
            {
                public Task<CommandResult> Handle(Refund command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
            }
            """ + Program("services.AddCqrsGenerated(b => b.UseLogging())"));

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA018").Which.GetMessage().Should().Contain("'PlaceOrder' and 'Refund'");
    }

    [Theory(DisplayName = "CQRA018 / CQRA019: nothing is reported for a configuration the analyzer cannot prove complete")]
    // A second registration, of any kind.
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging()); services.AddCqrsGenerated(b => b.UseTimeout(_ => { }))")]
    [InlineData("services.AddCqrsGenerated(); services.AddCqrsGenerated()")]
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging()); CqrsBuilderBootstrap.Build(services, b => b.UseLogging())")]
    // A configuration that is not a chain of builder verbs in view.
    [InlineData("services.AddCqrsGenerated(AppBuilderExtensions.Configure)")]
    [InlineData("services.AddCqrsGenerated(b => AppBuilderExtensions.Configure(b))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseAppDefaults())")]
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging().UseAppDefaults())")]
    [InlineData("Action<ICqrsBuilder> configure = b => b.UseLogging(); services.AddCqrsGenerated(configure)")]
    [InlineData("services.AddCqrsGenerated(b => { if (Environment.UserInteractive) b.UseIdempotency(); })")]
    [InlineData("services.AddCqrsGenerated(b => { for (var i = 0; i < 1; i++) b.UseIdempotency(); })")]
    [InlineData("services.AddCqrsGenerated(b => _ = Environment.UserInteractive ? b.UseIdempotency() : b)")]
    [InlineData("services.AddCqrsGenerated(b => b?.UseLogging())")]
    [InlineData("services.AddCqrsGenerated(b => { var other = b; other.UseLogging(); })")]
    [InlineData("services.AddCqrsGenerated(b => { void Local() => b.UseIdempotency(); Local(); })")]
    // An argument that could reach the service collection, a builder, or the application's own code while it is configured.
    [InlineData("services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseStore(s => s.AddTransient(typeof(IPipelineBehavior<,>), typeof(AppBehavior<,>)))))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseTimeout(o => { _ = b; }))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseTimeout(o => { _ = services; }))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseTimeout(AppBuilderExtensions.Tune))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseTimeout(o => AppBuilderExtensions.Tune(o)))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseTimeProvider(new AppClock()))")]
    // A registration handed on as a method group.
    [InlineData("Func<IServiceCollection, Action<ICqrsBuilder>, IServiceCollection> register = CqrsGeneratedBootstrap.AddCqrsGenerated; register(services, b => b.UseLogging())")]
    // A built-in behavior named, or an open behavior interface used other than in a plain registration.
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging()); services.AddTransient(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging()); services.AddTransient<IPipelineBehavior<PlaceOrder, CommandResult>, IdempotencyBehavior<PlaceOrder, CommandResult>>()")]
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging()); services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ResilienceBehavior<,>))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging()); foreach (var type in typeof(Program).Assembly.GetTypes()) services.AddTransient(typeof(IPipelineBehavior<,>), type)")]
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging()); Scan(typeof(IStreamPipelineBehavior<,>))")]
    public async Task Configuration_not_in_view_is_not_flagged(string registration)
    {
        var diagnostics = await AnalyzeAsync(Requests + """
            public sealed class AppClock : TimeProvider;
            public static partial class Program
            {
                private static void Scan(Type openInterface) { }
            }

            """ + Program(registration));

        diagnostics.Should().NotContain(d => d.Id == "CQRA018" || d.Id == "CQRA019");
    }

    [Fact(DisplayName = "CQRA018 / CQRA019: a library is not flagged, since the application that references it may configure CQRSharp further")]
    public async Task Library_is_not_flagged()
    {
        var run = CompilationHarness.RunGenerators([Application("services.AddCqrsGenerated()")], references: ProbeReferences.SingleProjectApplication());
        run.CompileErrors.Should().BeEmpty();

        (await CompilationHarness.AnalyzeAsync(run.Output, new MarkerWithoutBehaviorAnalyzer()))
            .Should().NotContain(d => d.Id == "CQRA018" || d.Id == "CQRA019");
    }

    [Theory(DisplayName = "CQRA018 / CQRA019: a referenced assembly that could configure CQRSharp takes the configuration out of view")]
    [InlineData("module")]
    [InlineData("pipelines")]
    [InlineData("abstractions-and-di")]
    [InlineData("scanner")]
    public async Task Referenced_assembly_that_can_configure_takes_it_out_of_view(string kind)
    {
        var library = kind switch
        {
            // A module: handlers, so a generated module, in an assembly that references CQRSharp.Core.
            "module" => Library("ModuleLib", """
                using System.Threading; using System.Threading.Tasks; using CQRSharp;
                namespace ModuleLib;
                public sealed class Audit : CommandBase;
                public sealed class AuditHandler : ICommandHandler<Audit>
                {
                    public Task<CommandResult> Handle(Audit command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
                }
                """),
            "pipelines" => Library("PipelinesLib", """
                using CQRSharp.Pipelines;
                namespace PipelinesLib;
                public static class Wiring { public static ICqrsBuilder Idempotent(ICqrsBuilder builder) => builder.UseIdempotency(); }
                """),
            "abstractions-and-di" => Library("SerializerLib", """
                using CQRSharp;
                using Microsoft.Extensions.DependencyInjection;
                namespace SerializerLib;
                public static class Wiring { public static IServiceCollection Mark(IServiceCollection services) => services; public static System.Type Contract => typeof(INotification); }
                """, ProbeReferences.AbstractionsOnly()),
            _ => Library("Scrutor", "namespace Scrutor; public static class Marker;",
                ProbeReferences.Create(name => name.StartsWith("CQRSharp", StringComparison.OrdinalIgnoreCase)))
        };

        var diagnostics = await AnalyzeAsync(Application("services.AddCqrsGenerated()"), library);

        diagnostics.Should().NotContain(d => d.Id == "CQRA018" || d.Id == "CQRA019");
    }

    [Fact(DisplayName = "CQRA018: a contracts assembly that references only CQRSharp.Abstractions keeps the configuration in view")]
    public async Task Contracts_assembly_keeps_it_in_view()
    {
        var contracts = Library("Contracts", """
            using CQRSharp;
            namespace Contracts;
            public sealed class Ship : CommandBase, IIdempotentRequest { public string IdempotencyKey => "ship"; }
            """, ProbeReferences.AbstractionsOnly());

        var diagnostics = await AnalyzeAsync(Requests + """
            public sealed class ShipHandler : ICommandHandler<Contracts.Ship>
            {
                public Task<CommandResult> Handle(Contracts.Ship command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
            }
            """ + Program("services.AddCqrsGenerated(b => b.UseLogging())"), contracts);

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA018").Which.GetMessage().Should().Contain("'Contracts.Ship'").And.Contain("'PlaceOrder'");
    }

    internal static string Program(string registration)
        => "public static partial class Program { public static void Main() { var services = new ServiceCollection(); " + registration + "; } }\n";

    internal static string Application(string registration) => Requests + Program(registration);

    internal static MetadataReference Library(string name, string source, MetadataReference[]? references = null)
    {
        var run = CompilationHarness.RunGenerators([source], assemblyName: name, references: references ?? ProbeReferences.SingleProjectApplication());
        run.CompileErrors.Should().BeEmpty();
        return run.Reference!;
    }

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, params MetadataReference[] references)
        => AnalyzeApplicationAsync(source, new MarkerWithoutBehaviorAnalyzer(), references);

    internal static Task<ImmutableArray<Diagnostic>> AnalyzeApplicationAsync(string source, Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer analyzer, params MetadataReference[] references)
    {
        var run = CompilationHarness.RunGenerators(
            [source], references, assemblyName: "App", references: ProbeReferences.SingleProjectApplication(), outputKind: OutputKind.ConsoleApplication);
        run.CompileErrors.Should().BeEmpty();
        return CompilationHarness.AnalyzeAsync(run.Output, analyzer);
    }
}
