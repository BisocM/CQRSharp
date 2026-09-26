using System.Reflection;
using CQRSharp.Shared;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     The one table of framework metadata names the generator and the analyzers recognize types by. Each entry must
///     resolve against the real assemblies, so a rename or a namespace move fails here instead of silently switching the
///     tooling off; the names are also what already-compiled generated modules bind to.
/// </summary>
public sealed class CqrsKnownSymbolsTests
{
    private static readonly string[] DeclaredNames = typeof(CqrsKnownSymbols.TypeNames)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToArray();

    [Fact(DisplayName = "Every name in the table is either required or optional, exactly once")]
    public void Table_is_partitioned()
    {
        var listed = CqrsKnownSymbols.TypeNames.Required.Concat(CqrsKnownSymbols.TypeNames.Optional).ToArray();

        listed.Should().OnlyHaveUniqueItems();
        listed.Should().BeEquivalentTo(DeclaredNames);
    }

    [Fact(DisplayName = "Every name resolves against the real assemblies, as the unbound definition of the declared arity")]
    public void Every_name_resolves()
    {
        var compilation = CompilationHarness.Compile(["// consumer"]);
        var known = CqrsKnownSymbols.For(compilation);

        foreach (var name in DeclaredNames)
        {
            var symbol = compilation.GetTypeByMetadataName(name);
            symbol.Should().NotBeNull($"'{name}' must name a framework type");
            symbol!.Should().BeSameAs(symbol.OriginalDefinition);
            var tick = name.IndexOf('`');
            symbol.Arity.Should().Be(tick < 0 ? 0 : int.Parse(name.Substring(tick + 1)), name);
        }

        known.CoreReferenced.Should().BeTrue();
        known.GetMissingRequiredTypeNames().Should().BeEmpty();
    }

    [Fact(DisplayName = "Required types live in CQRSharp.Abstractions or CQRSharp.Core, optional ones in CQRSharp.Pipelines")]
    public void Names_belong_to_the_expected_assemblies()
    {
        var compilation = CompilationHarness.Compile(["// consumer"]);

        foreach (var name in CqrsKnownSymbols.TypeNames.Required)
            compilation.GetTypeByMetadataName(name)!.ContainingAssembly.Name.Should().BeOneOf(["CQRSharp.Abstractions", "CQRSharp.Core"], name);
        foreach (var name in CqrsKnownSymbols.TypeNames.Optional)
            compilation.GetTypeByMetadataName(name)!.ContainingAssembly.Name.Should().Be("CQRSharp.Pipelines", name);
    }

    [Fact(DisplayName = "A resolved handler definition compare-equals a real implementation's interface definition")]
    public void Resolved_handler_matches_a_real_implementation()
    {
        var compilation = CompilationHarness.Compile(["""
            using System.Threading;
            using System.Threading.Tasks;
            using CQRSharp;

            public sealed class C : CommandBase;
            public sealed class H : ICommandHandler<C>
            {
                public Task<CommandResult> Handle(C command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
            }
            """]);
        var known = CqrsKnownSymbols.For(compilation);

        var iface = compilation.GetTypeByMetadataName("H")!.AllInterfaces.Single();

        SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, known.ICommandHandler).Should().BeTrue();
        known.IsDispatchHandler(iface).Should().BeTrue();
    }

    [Fact(DisplayName = "In a project that references only CQRSharp.Abstractions the handler types resolve and Core is absent")]
    public void Abstractions_only_project()
    {
        var known = CqrsKnownSymbols.For(CompilationHarness.Compile(["// abstractions only"], references: ProbeReferences.AbstractionsOnly()));

        known.CoreReferenced.Should().BeFalse();
        known.GetMissingRequiredTypeNames().Should().BeEmpty("without Core the generator has nothing to do, so nothing is missing");
        known.DispatchHandlerDefinitions.Select(d => d.Name).Should().BeEquivalentTo(
            "ICommandHandler", "IResultCommandHandler", "IQueryHandler", "IStreamRequestHandler", "INotificationHandler");
        known.CqrsGeneratedModuleAttribute.Should().NotBeNull();
        known.Dispatcher.Should().BeNull();
    }

    [Theory(DisplayName = "A request's pipeline-behavior service is closed over the result it is dispatched with")]
    [InlineData("public sealed class R : CommandBase;", "CQRSharp.Pipelines.IPipelineBehavior<R, CQRSharp.CommandResult>")]
    [InlineData("public sealed class R : ResultCommandBase<string>;", "CQRSharp.Pipelines.IPipelineBehavior<R, CQRSharp.CommandResult<string>>")]
    [InlineData("public sealed class R : QueryBase<int>;", "CQRSharp.Pipelines.IPipelineBehavior<R, int>")]
    [InlineData("public sealed class R : StreamRequestBase<int>;", "CQRSharp.Pipelines.IStreamPipelineBehavior<R, int>")]
    [InlineData("public sealed class R;", null)]
    public void Pipeline_behavior_service(string declaration, string? expected)
    {
        var compilation = CompilationHarness.Compile(["using CQRSharp;\n" + declaration]);

        var service = CqrsKnownSymbols.For(compilation).PipelineBehaviorServiceOf(compilation.GetTypeByMetadataName("R")!);

        service?.ToDisplayString().Should().Be(expected);
        if (expected is null) service.Should().BeNull();
    }

    [Theory(DisplayName = "A request's shape is read in one order: stream, query, value-returning command, command")]
    [InlineData("public sealed class R : CommandBase;", "Command", "CQRSharp.CommandResult", null)]
    [InlineData("public sealed class R : ResultCommandBase<string>;", "ResultCommand", "CQRSharp.CommandResult<string>", "CQRSharp.CommandResult<string>")]
    [InlineData("public sealed class R : QueryBase<int>;", "Query", "int", "int")]
    [InlineData("public sealed class R : StreamRequestBase<int>;", "Stream", "System.Collections.Generic.IAsyncEnumerable<int>", "int")]
    [InlineData("public sealed class R : QueryBase<int>, IStreamRequest<long>;", "Stream", "System.Collections.Generic.IAsyncEnumerable<long>", "long")]
    public void Request_shape(string declaration, string kind, string response, string? resultOrItem)
    {
        var compilation = CompilationHarness.Compile(["using CQRSharp;\n" + declaration]);

        var shape = CqrsKnownSymbols.For(compilation).ShapeOf(compilation.GetTypeByMetadataName("R")!);

        shape.Should().NotBeNull();
        shape!.Value.Kind.ToString().Should().Be(kind);
        shape.Value.Response.ToDisplayString().Should().Be(response);
        shape.Value.ResultOrItem?.ToDisplayString().Should().Be(resultOrItem);
        if (resultOrItem is null) shape.Value.ResultOrItem.Should().BeNull();
    }

    [Fact(DisplayName = "A type that is no request has no shape")]
    public void Non_request_has_no_shape()
    {
        var compilation = CompilationHarness.Compile(["public sealed class R;"]);

        CqrsKnownSymbols.For(compilation).ShapeOf(compilation.GetTypeByMetadataName("R")!).Should().BeNull();
    }

    [Fact(DisplayName = "The declared context is read through the request's base classes")]
    public void Declared_context()
    {
        var compilation = CompilationHarness.Compile(["""
            using CQRSharp;

            public sealed class TenantContext : RequestContextBase;
            public abstract class TenantCommand : CommandBase<TenantContext>;
            public sealed class R : TenantCommand;
            public sealed class Plain : CommandBase;
            public sealed class NotARequest;
            """]);
        var known = CqrsKnownSymbols.For(compilation);

        known.DeclaredContextOf(compilation.GetTypeByMetadataName("R")!)!.Name.Should().Be("TenantContext");
        known.DeclaredContextOf(compilation.GetTypeByMetadataName("Plain")!)!.Name.Should().Be("RequestContextBase");
        known.DeclaredContextOf(compilation.GetTypeByMetadataName("NotARequest")!).Should().BeNull();
    }
}
