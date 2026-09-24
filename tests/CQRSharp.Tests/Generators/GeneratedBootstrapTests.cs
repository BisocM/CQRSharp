using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     The generated <c>AddCqrsGenerated</c> bootstrap next to a referenced assembly's visible one: which call binds
///     where, and when CQRGEN015 flags it. Each case compiles what the generators emit.
/// </summary>
public sealed class GeneratedBootstrapTests
{
    private const string Usings = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;
using CQRSharp.Pipelines;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace ProbeNs;
";

    private const string AppSource = @"
using System.Threading;
using System.Threading.Tasks;
using CQRSharp;
using CQRSharp.Tests.Shared;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(""GeneratorProbe"")]
namespace AppNs;
public sealed class AppCommand : CommandBase;
public sealed class AppCommandHandler : ICommandHandler<AppCommand>
{
    public Task<CommandResult> Handle(AppCommand command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}";

    private const string ProbeHandler = @"
public sealed class ProbeCommand : CommandBase;
public sealed class ProbeCommandHandler : ICommandHandler<ProbeCommand>
{
    public Task<CommandResult> Handle(ProbeCommand command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}";

    [Fact(DisplayName = "With a referenced assembly's bootstrap visible, a plain AddCqrsGenerated() call in an assembly with its own module is a compile error and CQRGEN015 marks the call")]
    public void Plain_call_next_to_a_visible_foreign_bootstrap_is_loud()
    {
        var app = Run(new[] { AppSource }, assemblyName: "App");
        app.CompileErrors.Should().BeEmpty();

        var run = Run(new[]
        {
            Usings + ProbeHandler + @"
public static class Wiring
{
    public static void Wire(IServiceCollection services) => services.AddCqrsGenerated();
}"
        }, extraReferences: new[] { app.Reference! });

        run.CompileErrors.Should().Contain(e => e.StartsWith("CS0121", StringComparison.Ordinal));
        run.GeneratorDiagnostics.Should().Contain(d =>
            d.Id == "CQRGEN015" && d.Severity == DiagnosticSeverity.Warning && d.Location.GetLineSpan().Path == "Probe0.cs");
        run.GeneratorDiagnostics.Single(d => d.Id == "CQRGEN015").GetMessage().Should().Contain("CQRSharp.Generated.GeneratorProbe.CqrsGeneratedBootstrap.AddCqrsGenerated(services)");
    }

    [Fact(DisplayName = "The module-namespace entry point is unambiguous and registers this assembly's module")]
    public void Qualified_call_next_to_a_visible_foreign_bootstrap_compiles()
    {
        var app = Run(new[] { AppSource }, assemblyName: "App");

        var run = Run(new[]
        {
            Usings + ProbeHandler + @"
public static class Wiring
{
    public static void Wire(IServiceCollection services) => CQRSharp.Generated.GeneratorProbe.CqrsGeneratedBootstrap.AddCqrsGenerated(services);
}"
        }, extraReferences: new[] { app.Reference! });

        run.CompileErrors.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN015" && d.Severity == DiagnosticSeverity.Warning);
        run.Generated("CqrsGeneratedBootstrap.g.cs").Should().Contain("namespace CQRSharp.Generated.GeneratorProbe");
    }

    [Fact(DisplayName = "An assembly with no module of its own leaves the plain call to the one visible foreign bootstrap")]
    public void Plain_call_without_an_own_module_binds_to_the_foreign_bootstrap()
    {
        var app = Run(new[] { AppSource }, assemblyName: "App");

        var run = Run(new[]
        {
            Usings + @"
public static class Wiring
{
    public static void Wire(IServiceCollection services) => services.AddCqrsGenerated();
}"
        }, extraReferences: new[] { app.Reference! });

        run.CompileErrors.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN015" && d.Severity == DiagnosticSeverity.Warning);
        NamespacesOf(run.Generated("CqrsGeneratedBootstrap.g.cs")).Should().Equal(["CQRSharp.Generated.GeneratorProbe"],
            "a copy in namespace CQRSharp would make the plain call ambiguous with the visible one");
    }

    [Fact(DisplayName = "A call through an alias of the module-namespace bootstrap is not flagged by CQRGEN015")]
    public void Alias_call_is_not_flagged()
    {
        var app = Run(new[] { AppSource }, assemblyName: "App");

        var run = Run(new[]
        {
            "using Boot = CQRSharp.Generated.GeneratorProbe.CqrsGeneratedBootstrap;" + Usings + ProbeHandler + @"
public static class Wiring
{
    public static void Wire(IServiceCollection services) => Boot.AddCqrsGenerated(services);
}"
        }, extraReferences: new[] { app.Reference! });

        run.CompileErrors.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().NotContain(d => d.Id == "CQRGEN015" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "The bootstrap offers exactly AddCqrsGenerated() and AddCqrsGenerated(Action<ICqrsBuilder>)")]
    public void Bootstrap_offers_the_parameterless_and_builder_overloads_only()
    {
        var run = Run(new[]
        {
            Usings + ProbeHandler + @"
public static class Wiring
{
    public static void Plain(IServiceCollection services) => services.AddCqrsGenerated();
    public static void Built(IServiceCollection services) => services.AddCqrsGenerated(b => b.UseLogging());
}"
        });
        run.CompileErrors.Should().BeEmpty();

        var positional = Run(new[]
        {
            Usings + ProbeHandler + @"
public static class Wiring
{
    public static void Wire(IServiceCollection services) => services.AddCqrsGenerated(configureQueue: null);
}"
        });
        positional.CompileErrors.Should().NotBeEmpty("the positional-delegate overload is not generated");
    }

    [Fact(DisplayName = "The generated registrations are not a callable extension: services.AddGenerated() does not compile")]
    public void Registrations_are_not_callable_on_their_own()
    {
        var run = Run(new[]
        {
            Usings + ProbeHandler + @"
public static class Wiring
{
    public static void Wire(IServiceCollection services) => services.AddGenerated();
}"
        });

        run.CompileErrors.Should().Contain(e => e.StartsWith("CS1061", StringComparison.Ordinal));
        run.Generated("CqrsGeneratedBootstrap.g.cs").Should().NotContain("AddGenerated");
    }

    [Fact(DisplayName = "Beside a visible foreign bootstrap, the builder overload in the module namespace compiles")]
    public void Builder_overload_next_to_a_visible_foreign_bootstrap_compiles()
    {
        var app = Run(new[] { AppSource }, assemblyName: "App");

        var run = Run(new[]
        {
            Usings + ProbeHandler + @"
public static class Wiring
{
    public static void Wire(IServiceCollection services)
        => CQRSharp.Generated.GeneratorProbe.CqrsGeneratedBootstrap.AddCqrsGenerated(services, b => b.UseValidation(false));
}"
        }, extraReferences: new[] { app.Reference! });

        run.CompileErrors.Should().BeEmpty();
    }

    private static string[] NamespacesOf(string source)
        => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(declaration => declaration.Name.ToString())
            .ToArray();

    private static GeneratorRun Run(string[] sources, IEnumerable<MetadataReference>? extraReferences = null, string assemblyName = "GeneratorProbe")
        => CompilationHarness.RunGenerators(sources, extraReferences, assemblyName);
}
