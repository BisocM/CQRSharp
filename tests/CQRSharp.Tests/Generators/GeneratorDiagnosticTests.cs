using System.Collections.Immutable;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     Diagnostics the source generator reports (CQRGEN*). Driven through the generator driver so the exact reporting
///     path is exercised.
/// </summary>
public sealed class GeneratorDiagnosticTests
{
    private const string OpenGenericHandlerSource = @"
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;
using CQRSharp.Tests.Shared;

namespace ProbeNs;

public sealed class GenericCommand<T> : CommandBase;

// Open-generic handler: CQRSharp registers only closed handlers, so this is silently unwired — CQRGEN009 flags it.
public sealed class GenericCommandHandler<T> : ICommandHandler<GenericCommand<T>>
{
    public Task<CommandResult> Handle(GenericCommand<T> command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}
";

    private const string ClosedHandlerSource = @"
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;

namespace ProbeNs;

public sealed class PlainCommand : CommandBase;

public sealed class PlainCommandHandler : ICommandHandler<PlainCommand>
{
    public Task<CommandResult> Handle(PlainCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}
";

    [Fact(DisplayName = "CQRGEN009: an open-generic handler is flagged")]
    public void OpenGenericHandler_RaisesCqrgen009()
    {
        var diagnostics = RunGenerator(OpenGenericHandlerSource);
        diagnostics.Should().Contain(d => d.Id == "CQRGEN009",
            "an open-generic handler is never registered and would otherwise fail only at dispatch");
    }

    [Fact(DisplayName = "CQRGEN009: a closed (concrete) handler is not flagged")]
    public void ClosedHandler_DoesNotRaiseCqrgen009()
    {
        var diagnostics = RunGenerator(ClosedHandlerSource);
        diagnostics.Should().NotContain(d => d.Id == "CQRGEN009");
    }

    private const string NotificationUsings = @"
using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;

namespace ProbeNs;
";

    [Fact(DisplayName = "CQRGEN005: a required member excluded from the payload makes the notification unserializable")]
    public void Required_excluded_member_RaisesCqrgen005()
    {
        var diagnostics = RunGenerator(NotificationUsings + @"
[NotificationName(""acct.moved"")]
public sealed record AccountMoved : INotification
{
    public Guid AccountId { get; init; }
    [JsonIgnore] public required string Secret { get; init; }
}
public sealed class AccountMovedHandler : INotificationHandler<AccountMoved>
{
    public Task Handle(AccountMoved notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        var message = diagnostics.Should().ContainSingle(d => d.Id == "CQRGEN005").Which.GetMessage();
        message.Should().Contain("required");
        message.Should().NotContain("..", "the reason ends its own sentence");
    }

    [Fact(DisplayName = "CQRGEN005: two properties mapped to one JSON name make the payload ambiguous")]
    public void Colliding_json_names_RaiseCqrgen005()
    {
        var diagnostics = RunGenerator(NotificationUsings + @"
[NotificationName(""order.priced"")]
public sealed record OrderPriced : INotification
{
    public decimal Amount { get; init; }
    [JsonPropertyName(""Amount"")] public decimal Total { get; init; }
}
public sealed class OrderPricedHandler : INotificationHandler<OrderPriced>
{
    public Task Handle(OrderPriced notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        diagnostics.Should().ContainSingle(d => d.Id == "CQRGEN005").Which.GetMessage().Should().Contain("Amount");
    }

    [Fact(DisplayName = "CQRGEN011: PartitionBy naming a property that does not exist is an error")]
    public void PartitionBy_without_a_matching_property_RaisesCqrgen011()
    {
        var diagnostics = RunGenerator(NotificationUsings + @"
[NotificationName(""order.created"", PartitionBy = ""Missing"")]
public sealed record OrderCreated(Guid OrderId) : INotification;
public sealed class OrderCreatedHandler : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "CQRGEN011").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("Missing").And.Contain("IPartitionedNotification");
    }

    [Fact(DisplayName = "CQRGEN011: PartitionBy naming an existing property is not flagged")]
    public void PartitionBy_with_a_matching_property_DoesNotRaiseCqrgen011()
    {
        var diagnostics = RunGenerator(NotificationUsings + @"
[NotificationName(""order.created"", PartitionBy = nameof(OrderId))]
public sealed record OrderCreated(Guid OrderId) : INotification;
public sealed class OrderCreatedHandler : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        diagnostics.Should().NotContain(d => d.Id == "CQRGEN011");
    }

    [Fact(DisplayName = "CQRGEN012: two handlers pinned to the same name are an error at both declarations")]
    public void Duplicate_handler_names_RaiseCqrgen012()
    {
        var diagnostics = RunGenerator(NotificationUsings + @"
public sealed record OrderCreated(Guid OrderId) : INotification;
[NotificationHandlerName(""shared"")]
public sealed class FirstHandler : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}
[NotificationHandlerName(""shared"")]
public sealed class SecondHandler : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        var duplicates = diagnostics.Where(d => d.Id == "CQRGEN012").ToList();
        duplicates.Should().HaveCount(2, "each offending handler is flagged at its own declaration");
        duplicates.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Error && d.GetMessage().Contains("shared"));
    }

    [Fact(DisplayName = "CQRGEN013: a blank [NotificationHandlerName] is a warning and the type name is used instead")]
    public void Blank_handler_name_RaisesCqrgen013()
    {
        var diagnostics = RunGenerator(NotificationUsings + @"
public sealed record OrderCreated(Guid OrderId) : INotification;
[NotificationHandlerName(""   "")]
public sealed class BlankHandler : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "CQRGEN013").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostics.Should().NotContain(d => d.Id == "CQRGEN012");
    }

    [Fact(DisplayName = "CQRGEN014: an idempotent request whose payload cannot be rendered is reported at Info")]
    public void Unfingerprintable_idempotent_request_RaisesCqrgen014()
    {
        var diagnostics = RunGenerator(NotificationUsings + @"
public sealed class Charge : CommandBase, IIdempotentRequest
{
    public int[,] Grid { get; init; } = new int[0, 0];
    public string IdempotencyKey => ""k"";
}
public sealed class ChargeHandler : ICommandHandler<Charge>
{
    public Task<CommandResult> Handle(Charge command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}");

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "CQRGEN014").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Info);
        diagnostic.GetMessage().Should().Contain("Charge").And.Contain("IFingerprintedRequest").And.NotContain("..");
    }

    [Fact(DisplayName = "CQRGEN014: a request that fingerprints itself, or whose payload renders, is not flagged")]
    public void Fingerprintable_requests_DoNotRaiseCqrgen014()
    {
        var diagnostics = RunGenerator(NotificationUsings + @"
public sealed class Charge : CommandBase, IIdempotentRequest
{
    public Guid AccountId { get; init; }
    public decimal Amount { get; init; }
    public string IdempotencyKey => ""k"";
}
public sealed class ChargeHandler : ICommandHandler<Charge>
{
    public Task<CommandResult> Handle(Charge command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}
public sealed class Transfer : CommandBase, IFingerprintedRequest
{
    public int[,] Grid { get; init; } = new int[0, 0];
    public string IdempotencyKey => ""k"";
    public string Fingerprint => ""self"";
}
public sealed class TransferHandler : ICommandHandler<Transfer>
{
    public Task<CommandResult> Handle(Transfer command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}");

        diagnostics.Should().NotContain(d => d.Id == "CQRGEN014");
    }

    [Fact(DisplayName = "CQRGEN002: two notifications sharing one [NotificationName] are an error naming the name and both types")]
    public void Duplicate_notification_names_raise_Cqrgen002()
    {
        var diagnostics = RunGenerator(NotificationUsings + @"
[NotificationName(""dup"")]
public sealed record First(Guid Id) : INotification;
[NotificationName(""dup"")]
public sealed record Second(Guid Id) : INotification;");

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "CQRGEN002").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("'dup'").And.Contain("ProbeNs.First").And.Contain("ProbeNs.Second");
    }

    [Fact(DisplayName = "CQRGEN003: a request declared here with no handler is a warning at the request")]
    public void Request_without_a_handler_raises_Cqrgen003()
    {
        var diagnostics = RunGenerator(NotificationUsings + "public sealed class Orphan : CommandBase;");

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "CQRGEN003").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Contain("ProbeNs.Orphan");
        diagnostic.Location.GetLineSpan().Path.Should().Be("Probe0.cs");
    }

    [Fact(DisplayName = "CQRGEN004: a request with two handlers is one error, at the handler that is bound, naming both")]
    public void Two_handlers_raise_Cqrgen004()
    {
        var source = NotificationUsings + @"
public sealed class Ping : CommandBase;
public sealed class AlphaHandler : ICommandHandler<Ping>
{
    public Task<CommandResult> Handle(Ping command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}
public sealed class BetaHandler : ICommandHandler<Ping>
{
    public Task<CommandResult> Handle(Ping command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}";
        var diagnostics = RunGenerator(source);

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "CQRGEN004").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("ProbeNs.AlphaHandler").And.Contain("ProbeNs.BetaHandler");
        var alphaLine = Array.FindIndex(source.Split('\n'), line => line.Contains("class AlphaHandler"));
        diagnostic.Location.GetLineSpan().StartLinePosition.Line.Should().Be(alphaLine,
            "the ordinal-first handler is the one bound, and the diagnostic sits on it");
    }

    [Fact(DisplayName = "CQRGEN006: a handler nested privately is a warning at the handler")]
    public void Private_nested_handler_raises_Cqrgen006()
    {
        var diagnostics = RunGenerator(NotificationUsings + @"
public sealed class Ping : CommandBase;
public static class Outer
{
    private sealed class PingHandler : ICommandHandler<Ping>
    {
        public Task<CommandResult> Handle(Ping command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
    }
}");

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "CQRGEN006").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Contain("Outer.PingHandler");
    }

    [Fact(DisplayName = "CQRGEN017: an exception handler declared over a wider response than its request's is an error, and no hook is emitted for it")]
    public void Exception_handler_over_a_wider_response_raises_Cqrgen017()
    {
        var run = CompilationHarness.RunGenerators([NotificationUsings + @"
public sealed class CreateKey : ResultCommandBase<string>;
public sealed class CreateKeyHandler : IResultCommandHandler<CreateKey, string>
{
    public Task<CommandResult<string>> Handle(CreateKey command, CancellationToken cancellationToken) => Task.FromResult(CommandResult<string>.FromSuccess(""k""));
}
public sealed class CreateKeyErrors : IRequestExceptionHandler<CreateKey, CommandResult, InvalidOperationException>
{
    public Task Handle(CreateKey request, InvalidOperationException exception, RequestExceptionHandlerState<CommandResult> state, CancellationToken cancellationToken) => Task.CompletedTask;
}
public sealed class GetName : QueryBase<string>;
public sealed class GetNameHandler : IQueryHandler<GetName, string>
{
    public Task<string> Handle(GetName query, CancellationToken cancellationToken) => Task.FromResult(""n"");
}
public sealed class GetNameErrors : IRequestExceptionHandler<GetName, object, InvalidOperationException>
{
    public Task Handle(GetName request, InvalidOperationException exception, RequestExceptionHandlerState<object> state, CancellationToken cancellationToken) => Task.CompletedTask;
}"]);

        run.CompileErrors.Should().BeEmpty();
        var mismatches = run.GeneratorDiagnostics.Where(d => d.Id == "CQRGEN017").ToArray();
        mismatches.Should().HaveCount(2).And.OnlyContain(d => d.Severity == DiagnosticSeverity.Error);
        mismatches.Should().Contain(d => d.GetMessage().Contains("CreateKeyErrors") && d.GetMessage().Contains("CQRSharp.CommandResult<string>"));
        mismatches.Should().Contain(d => d.GetMessage().Contains("GetNameErrors") && d.GetMessage().Contains("'object'"));
        run.Generated("CqrsModule.g.cs").Should().NotContain("RequestExceptionHook.For<");
    }

    [Fact(DisplayName = "CQRGEN017: a request handler declared over a wider result than its query's is an error, and it is not bound")]
    public void Request_handler_over_a_wider_result_raises_Cqrgen017()
    {
        var run = CompilationHarness.RunGenerators([NotificationUsings + @"
public sealed class GetName : QueryBase<string>;
public sealed class GetNameHandler : IQueryHandler<GetName, object>
{
    public Task<object> Handle(GetName query, CancellationToken cancellationToken) => Task.FromResult<object>(""n"");
}"]);

        run.CompileErrors.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "CQRGEN017")
            .Which.GetMessage().Should().Contain("GetNameHandler").And.Contain("'object'").And.Contain("'string'");
        run.GeneratorDiagnostics.Should().Contain(d => d.Id == "CQRGEN003", "without its only handler the request is unhandled");
        run.Generated("CqrsModule.g.cs").Should().NotContain("[typeof(global::ProbeNs.GetName)] = new global::CQRSharp.Core.Registries.RequestMetadata(");
    }

    [Fact(DisplayName = "CQRGEN016: an interceptor or exemption naming a type generated code cannot name is an error, not a silently dropped attribute")]
    public void Unrenderable_request_attribute_raises_Cqrgen016()
    {
        var run = CompilationHarness.RunGenerators([NotificationUsings + @"
using CQRSharp.Pipelines;

public sealed class PolicyAttribute(Type policy) : Attribute, IPreHandlerAttribute
{
    public int PreHandlerExecutionPriority => 0;
    public Task OnBeforeHandle(IRequest request, IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;
}
public static class Feature
{
    private sealed class Secret;
    private sealed class Hidden<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
    {
        public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
    }

    [Policy(typeof(Secret))]
    [PipelineExemption(typeof(Hidden<,>))]
    public sealed class Ping : CommandBase;
}
public sealed class PingHandler : ICommandHandler<Feature.Ping>
{
    public Task<CommandResult> Handle(Feature.Ping command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}"]);

        run.CompileErrors.Should().BeEmpty();
        var skipped = run.GeneratorDiagnostics.Where(d => d.Id == "CQRGEN016").ToArray();
        skipped.Should().HaveCount(2).And.OnlyContain(d => d.Severity == DiagnosticSeverity.Error);
        skipped.Should().Contain(d => d.GetMessage().Contains("PolicyAttribute") && d.GetMessage().Contains("Feature.Secret"));
        skipped.Should().Contain(d => d.GetMessage().Contains("PipelineExemptionAttribute") && d.GetMessage().Contains("Feature.Hidden"));
    }

    [Fact(DisplayName = "Without CQRSharp.Core the generator emits nothing and reports nothing")]
    public void Abstractions_only_project_is_skipped()
    {
        var run = CompilationHarness.RunGenerators([ClosedHandlerSource], references: ProbeReferences.AbstractionsOnly());

        run.CompileErrors.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().BeEmpty();
        run.Sources.Keys.Should().NotContain(["CqrsModule.g.cs", "CqrsGeneratedBootstrap.g.cs", "CqrsGeneratedAssemblyMarkers.g.cs"]);
    }

    [Fact(DisplayName = "CQRGEN007: a framework type that cannot be resolved stops generation with an error naming it")]
    public void Unresolvable_framework_type_raises_Cqrgen007()
    {
        // A second assembly that also defines a framework type makes its metadata name ambiguous, as a mismatched or
        // repackaged CQRSharp would.
        var shadow = CompilationHarness.Compile(["namespace CQRSharp { public interface IPartitionedNotification; }"], assemblyName: "Shadow");
        using var image = new MemoryStream();
        shadow.Emit(image, cancellationToken: TestContext.Current.CancellationToken).Success.Should().BeTrue();

        var run = CompilationHarness.RunGenerators([ClosedHandlerSource], [MetadataReference.CreateFromImage(image.ToArray())]);

        run.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "CQRGEN007")
            .Which.GetMessage().Should().Contain("CQRSharp.IPartitionedNotification");
        run.Sources.Keys.Should().NotContain("CqrsModule.g.cs").And.NotContain("CqrsGeneratedBootstrap.g.cs");
    }

    private const string ContextFactoryUsings = @"
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;
";

    [Fact(DisplayName = "CQRGEN018: two context factories for one context type in one assembly are an error at each; a generic one is not registered and does not count")]
    public void Duplicate_context_factories_raise_Cqrgen018()
    {
        var diagnostics = RunGenerator(ContextFactoryUsings + @"
namespace ProbeNs;

public sealed class TenantContext : RequestContextBase;

public sealed class FirstFactory : IRequestContextFactory<TenantContext>
{
    public ValueTask<TenantContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new TenantContext());
}

public sealed class SecondFactory : IRequestContextFactory<TenantContext>
{
    public ValueTask<TenantContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new TenantContext());
}

public sealed class GenericFactory<T> : IRequestContextFactory<TenantContext>
{
    public ValueTask<TenantContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new TenantContext());
}");

        var duplicates = diagnostics.Where(d => d.Id == "CQRGEN018").ToArray();
        duplicates.Select(d => d.Location.GetLineSpan().StartLinePosition.Line).Should().HaveCount(2).And.OnlyHaveUniqueItems("one at each factory");
        duplicates.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Error);
        duplicates[0].GetMessage().Should().Contain("ProbeNs.TenantContext").And.Contain("ProbeNs.FirstFactory").And.Contain("ProbeNs.SecondFactory")
            .And.NotContain("GenericFactory");
    }

    [Fact(DisplayName = "CQRGEN019: factories for one context type in two referenced assemblies, and none here, are a warning where this assembly composes them")]
    public void Ambiguous_referenced_context_factories_raise_Cqrgen019()
    {
        var contracts = CompilationHarness.RunGenerators([ContextFactoryUsings + "namespace Contracts; public sealed class TenantContext : RequestContextBase;"], assemblyName: "Contracts");
        contracts.CompileErrors.Should().BeEmpty();
        var libA = ContextFactoryLibrary("LibA", "public sealed class TenantFactory", contracts);
        var libB = ContextFactoryLibrary("LibB", "public sealed class TenantFactory", contracts);
        var libGeneric = ContextFactoryLibrary("LibGeneric", "public sealed class TenantFactory<T>", contracts);

        var run = CompilationHarness.RunGenerators([Composition()], [contracts.Reference!, libA.Reference!, libB.Reference!]);

        run.CompileErrors.Should().BeEmpty();
        var diagnostic = run.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "CQRGEN019").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Location.GetLineSpan().Path.Should().Be("Probe0.cs", "it is reported at the AddCqrsGenerated call that composes the two");
        diagnostic.GetMessage().Should().Contain("'Contracts.TenantContext'")
            .And.Contain("'LibA.TenantFactory' in 'LibA'").And.Contain("'LibB.TenantFactory' in 'LibB'")
            .And.Contain("'LibB.TenantFactory' creates its contexts", "LibB's module registers after LibA's");

        CompilationHarness.RunGenerators([Composition(ownFactory: true)], [contracts.Reference!, libA.Reference!, libB.Reference!])
            .GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN019", "this assembly's own factory replaces theirs");
        CompilationHarness.RunGenerators([ContextFactoryUsings + "namespace Host; public sealed class NotComposing;"], [contracts.Reference!, libA.Reference!, libB.Reference!])
            .GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN019", "an assembly that never calls AddCqrsGenerated composes nothing");
        CompilationHarness.RunGenerators([Composition()], [contracts.Reference!, libA.Reference!, libGeneric.Reference!])
            .GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN019", "a generic factory is not registered by generated code");

        static string Composition(bool ownFactory = false) => ContextFactoryUsings + @"
using Microsoft.Extensions.DependencyInjection;

namespace Host;

public static class Wiring
{
    public static void Wire(IServiceCollection services) => services.AddCqrsGenerated();
}
" + (ownFactory
            ? @"
public sealed class HostTenantFactory : IRequestContextFactory<Contracts.TenantContext>
{
    public ValueTask<Contracts.TenantContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new Contracts.TenantContext());
}"
            : "");
    }

    private static GeneratorRun ContextFactoryLibrary(string name, string declaration, GeneratorRun contracts)
    {
        var library = CompilationHarness.RunGenerators([ContextFactoryUsings + $@"
namespace {name};

{declaration} : IRequestContextFactory<Contracts.TenantContext>
{{
    public ValueTask<Contracts.TenantContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new Contracts.TenantContext());
}}"], [contracts.Reference!], assemblyName: name);
        library.CompileErrors.Should().BeEmpty();
        return library;
    }

    // Every input here is legal code, so the generated project must compile too.
    private static ImmutableArray<Diagnostic> RunGenerator(string source)
    {
        var run = CompilationHarness.RunGenerators([source]);
        run.CompileErrors.Should().BeEmpty();
        return run.GeneratorDiagnostics;
    }
}
