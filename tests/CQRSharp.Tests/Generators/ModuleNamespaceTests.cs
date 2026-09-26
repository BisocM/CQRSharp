using CQRSharp.Shared;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     The namespace each assembly's generated module lives in. Two assemblies of one reference graph must never get
///     the same one: the host's generated types would shadow the library's, and the library's module would silently
///     never be registered.
/// </summary>
public sealed class ModuleNamespaceTests
{
    [Theory(DisplayName = "An assembly name that is a valid dotted identifier maps onto itself, one segment per part")]
    [InlineData("Orders.Api", "CQRSharp.Generated.Orders.Api")]
    [InlineData("Orders_Api", "CQRSharp.Generated.Orders_Api")]
    [InlineData("订单.Api", "CQRSharp.Generated.订单.Api")]
    [InlineData("GeneratorProbe", "CQRSharp.Generated.GeneratorProbe")]
    public void Lossless_names_are_kept(string assemblyName, string expected)
        => CqrsKnownSymbols.ModuleNamespaceFor(assemblyName).Should().Be(expected);

    [Theory(DisplayName = "A part that is no identifier as it stands is made one, and the last segment carries a hash of the exact name")]
    [InlineData("Orders-Api", "CQRSharp.Generated.Orders_Api_")]
    [InlineData("1Password.Core", "CQRSharp.Generated._1Password.Core_")]
    [InlineData("My.class", "CQRSharp.Generated.My.class__")]
    [InlineData("Orders..Api", "CQRSharp.Generated.Orders._.Api_")]
    public void Lossy_names_carry_a_hash(string assemblyName, string expectedPrefix)
    {
        var ns = CqrsKnownSymbols.ModuleNamespaceFor(assemblyName);

        ns.Should().StartWith(expectedPrefix).And.HaveLength(expectedPrefix.Length + 8);
        ns.Substring(expectedPrefix.Length).Should().MatchRegex("^[0-9A-F]{8}$");
        CqrsKnownSymbols.ModuleNamespaceFor(assemblyName).Should().Be(ns, "the hash is stable");
    }

    [Fact(DisplayName = "Names that make the same identifiers get distinct namespaces")]
    public void Colliding_names_are_distinct()
    {
        string[] names = ["Orders.Api", "Orders_Api", "Orders-Api", "Orders+Api", "Orders Api", "订单.Api", "库存.Api", "Orders\u200BApi", "OrdersApi"];

        names.Select(CqrsKnownSymbols.ModuleNamespaceFor).Should().OnlyHaveUniqueItems();
    }

    [Fact(DisplayName = "No name, or an empty one, maps to the anonymous namespace")]
    public void Missing_name_is_anonymous()
    {
        CqrsKnownSymbols.ModuleNamespaceFor(null).Should().Be("CQRSharp.Generated.Anonymous");
        CqrsKnownSymbols.ModuleNamespaceFor("").Should().Be("CQRSharp.Generated.Anonymous");
    }

    [Fact(DisplayName = "A host and a referenced library whose names differ only in punctuation each register their own module")]
    public void Library_and_host_with_similar_names_both_register()
    {
        var library = Module("Orders.Api", "PlaceOrder");
        var host = CompilationHarness.RunGenerators([Handler("HostNs", "Restock") + @"
namespace HostNs
{
    public static class Wiring
    {
        public static void Wire(Microsoft.Extensions.DependencyInjection.IServiceCollection services) => services.AddCqrsGenerated();
    }
}"], [library], assemblyName: "Orders_Api");

        host.CompileErrors.Should().BeEmpty();
        host.Output.GetDiagnostics(TestContext.Current.CancellationToken).Should().NotContain(d => d.Id == "CS0436",
            "the host's generated types must not shadow the library's");
        var bootstrap = host.Generated("CqrsGeneratedBootstrap.g.cs");
        bootstrap.Should().Contain("global::CQRSharp.Generated.Orders.Api.CqrsModuleRegistrar.Register(services);")
            .And.Contain("global::CQRSharp.Generated.Orders_Api.CqrsModuleRegistrar.Register(services);");
    }

    [Fact(DisplayName = "Two referenced libraries whose names map to the same identifiers both register, without an ambiguity")]
    public void Two_libraries_with_similar_names_both_register()
    {
        var first = Module("Orders-Api", "PlaceOrder");
        var second = Module("Orders+Api", "CancelOrder");

        var host = CompilationHarness.RunGenerators([Handler("HostNs", "Restock")], [first, second], assemblyName: "Host");

        host.CompileErrors.Should().BeEmpty("the two libraries' registrars must not be ambiguous (CS0433)");
        var bootstrap = host.Generated("CqrsGeneratedBootstrap.g.cs");
        bootstrap.Should().Contain($"global::{CqrsKnownSymbols.ModuleNamespaceFor("Orders-Api")}.CqrsModuleRegistrar.Register(services);")
            .And.Contain($"global::{CqrsKnownSymbols.ModuleNamespaceFor("Orders+Api")}.CqrsModuleRegistrar.Register(services);");
    }

    private static MetadataReference Module(string assemblyName, string command)
    {
        var run = CompilationHarness.RunGenerators([Handler("Lib" + command, command)], assemblyName: assemblyName);
        run.CompileErrors.Should().BeEmpty();
        return run.Reference!;
    }

    private static string Handler(string ns, string command) => $@"
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;

namespace {ns}
{{
    public sealed class {command} : CommandBase;
    public sealed class {command}Handler : ICommandHandler<{command}>
    {{
        public Task<CommandResult> Handle({command} command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
    }}
}}";
}
