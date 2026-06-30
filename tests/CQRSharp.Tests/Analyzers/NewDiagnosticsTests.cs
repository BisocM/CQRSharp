using System.Collections.Immutable;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Analyzers;
using CQRSharp.Core.Mediation;
using CQRSharp.Pipelines.Extensions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     Verifies the 4.1.0 ease-of-use analyzers (CQRA011–CQRA014, CQRA017) by compiling snippets in-memory against the
///     real CQRSharp assemblies and asserting the reported diagnostics.
/// </summary>
public class NewDiagnosticsTests
{
    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, params DiagnosticAnalyzer[] analyzers)
    {
        var paths = new HashSet<string>(
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p)),
            StringComparer.OrdinalIgnoreCase)
        {
            typeof(IRequest).Assembly.Location,
            typeof(ICqrsDispatcher).Assembly.Location,
            typeof(ICqrsBuilder).Assembly.Location,
            typeof(IServiceCollection).Assembly.Location
        };

        var references = paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();

        var compilation = CSharpCompilation.Create(
            "AnalyzerUnderTest",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (compileErrors.Length > 0)
            throw new InvalidOperationException("Test snippet failed to compile: " +
                                                string.Join(" | ", compileErrors.Select(e => e.ToString())));

        var withAnalyzers = compilation.WithAnalyzers(analyzers.ToImmutableArray());
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    [Fact(DisplayName = "CQRA014: a direct AddCqrs() call is flagged as an error")]
    public async Task CQRA014_FlagsDirectAddCqrs()
    {
        const string source = """
                              using CQRSharp.Core.Extensions;
                              using Microsoft.Extensions.DependencyInjection;

                              public class Startup
                              {
                                  public void Configure(IServiceCollection services) => services.AddCqrs();
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new DirectCoreRegistrationAnalyzer());
        diagnostics.Should().ContainSingle(d => d.Id == "CQRA014" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "CQRA014: an unrelated registration call is not flagged")]
    public async Task CQRA014_DoesNotFlagUnrelatedCall()
    {
        const string source = """
                              using Microsoft.Extensions.DependencyInjection;

                              public class Startup
                              {
                                  public void Configure(IServiceCollection services) => services.AddSingleton<string>("x");
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new DirectCoreRegistrationAnalyzer());
        diagnostics.Should().NotContain(d => d.Id == "CQRA014");
    }

    [Fact(DisplayName = "CQRA011: a custom-context request without a factory is flagged")]
    public async Task CQRA011_FlagsCustomContextWithoutFactory()
    {
        const string source = """
                              using System;
                              using CQRSharp.Abstractions.Interfaces.Context;
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;

                              public sealed class MyContext : IRequestContext
                              {
                                  public DateTime CreatedAt => default;
                              }

                              public sealed class FancyCommand : CommandBase<MyContext> { }
                              """;

        var diagnostics = await AnalyzeAsync(source, new MissingContextFactoryAnalyzer());
        diagnostics.Should().ContainSingle(d => d.Id == "CQRA011");
    }

    [Fact(DisplayName = "CQRA011: a custom-context request with a factory present is not flagged")]
    public async Task CQRA011_DoesNotFlagWhenFactoryPresent()
    {
        const string source = """
                              using System;
                              using CQRSharp.Abstractions.Interfaces.Context;
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;
                              using CQRSharp.Abstractions.Interfaces.Markers.Request;
                              using CQRSharp.Core.Factories;

                              public sealed class MyContext : IRequestContext
                              {
                                  public DateTime CreatedAt => default;
                              }

                              public sealed class FancyCommand : CommandBase<MyContext> { }

                              public sealed class MyContextFactory : IRequestContextFactory<MyContext>
                              {
                                  public MyContext CreateContext(IRequest request) => new MyContext();
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new MissingContextFactoryAnalyzer());
        diagnostics.Should().NotContain(d => d.Id == "CQRA011");
    }

    [Fact(DisplayName = "CQRA011: the default context (CommandBase) is not flagged")]
    public async Task CQRA011_DoesNotFlagDefaultContext()
    {
        const string source = """
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;

                              public sealed class PlainCommand : CommandBase { }
                              """;

        var diagnostics = await AnalyzeAsync(source, new MissingContextFactoryAnalyzer());
        diagnostics.Should().NotContain(d => d.Id == "CQRA011");
    }

    [Fact(DisplayName = "CQRA012: a validator with CQRSharp registered but no pack is flagged")]
    public async Task CQRA012_FlagsValidatorWithoutPack()
    {
        const string source = """
                              using System;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;
                              using CQRSharp.Abstractions.Interfaces.Validation;
                              using CQRSharp.Abstractions.Models.Validation;
                              using CQRSharp.Core.Extensions;
                              using Microsoft.Extensions.DependencyInjection;

                              public sealed class MyCommand : CommandBase { }

                              public sealed class MyValidator : IRequestValidator<MyCommand>
                              {
                                  public Task<ValidationFailure[]> ValidateAsync(MyCommand request, CancellationToken cancellationToken)
                                      => Task.FromResult(Array.Empty<ValidationFailure>());
                              }

                              public class Startup
                              {
                                  public void Configure(IServiceCollection services) => services.AddCqrs();
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new ValidatorWithoutValidationAnalyzer());
        diagnostics.Should().ContainSingle(d => d.Id == "CQRA012");
    }

    [Fact(DisplayName = "CQRA012: a validator with UseValidation present is not flagged")]
    public async Task CQRA012_DoesNotFlagWhenValidationEnabled()
    {
        const string source = """
                              using System;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;
                              using CQRSharp.Abstractions.Interfaces.Validation;
                              using CQRSharp.Abstractions.Models.Validation;
                              using CQRSharp.Core.Extensions;
                              using CQRSharp.Pipelines.Extensions;
                              using Microsoft.Extensions.DependencyInjection;

                              public sealed class MyCommand : CommandBase { }

                              public sealed class MyValidator : IRequestValidator<MyCommand>
                              {
                                  public Task<ValidationFailure[]> ValidateAsync(MyCommand request, CancellationToken cancellationToken)
                                      => Task.FromResult(Array.Empty<ValidationFailure>());
                              }

                              public class Startup
                              {
                                  public void Configure(IServiceCollection services) => services.AddCqrs();
                                  public void Build(ICqrsBuilder builder) => builder.UseValidation();
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new ValidatorWithoutValidationAnalyzer());
        diagnostics.Should().NotContain(d => d.Id == "CQRA012");
    }

    [Fact(DisplayName = "CQRA013: a non-retryable request is flagged (Info) when resilience is configured")]
    public async Task CQRA013_FlagsRequestWithoutMarkerWhenConfigured()
    {
        const string source = """
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;
                              using CQRSharp.Pipelines.Extensions;

                              public sealed class PlainCommand : CommandBase { }

                              public class Startup
                              {
                                  public void Build(ICqrsBuilder builder) => builder.UseResilience(o => { });
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new BehaviorMarkerMissingAnalyzer());
        diagnostics.Should().Contain(d => d.Id == "CQRA013" && d.Severity == DiagnosticSeverity.Info);
    }

    [Fact(DisplayName = "CQRA013: nothing is flagged when no behavior is configured")]
    public async Task CQRA013_DoesNotFlagWhenNotConfigured()
    {
        const string source = """
                              using CQRSharp.Abstractions.Interfaces.Markers.Command;

                              public sealed class PlainCommand : CommandBase { }
                              """;

        var diagnostics = await AnalyzeAsync(source, new BehaviorMarkerMissingAnalyzer());
        diagnostics.Should().NotContain(d => d.Id == "CQRA013");
    }

    [Fact(DisplayName = "CQRA017: a pre+post attribute pair is nudged toward ICommandInterceptor")]
    public async Task CQRA017_FlagsPrePostPair()
    {
        const string source = """
                              using System;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using CQRSharp.Abstractions.Attributes.Pipelines;
                              using CQRSharp.Abstractions.Interfaces.Markers.Request;

                              public sealed class BothAttribute : Attribute, IPreHandlerAttribute, IPostHandlerAttribute
                              {
                                  public int PreHandlerExecutionPriority => 0;
                                  public int PostHandlerExecutionPriority => 0;
                                  public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken) => Task.CompletedTask;
                                  public Task OnAfterHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken) => Task.CompletedTask;
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new CommandInterceptorGuidanceAnalyzer());
        diagnostics.Should().ContainSingle(d => d.Id == "CQRA017" && d.Severity == DiagnosticSeverity.Info);
    }

    [Fact(DisplayName = "CQRA017: an ICommandInterceptor is not flagged")]
    public async Task CQRA017_DoesNotFlagCommandInterceptor()
    {
        const string source = """
                              using System;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using CQRSharp.Abstractions.Attributes.Pipelines;
                              using CQRSharp.Abstractions.Interfaces.Markers.Request;

                              public sealed class CombinedAttribute : Attribute, ICommandInterceptor
                              {
                                  public int PreHandlerExecutionPriority => 0;
                                  public int PostHandlerExecutionPriority => 0;
                                  public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken) => Task.CompletedTask;
                                  public Task OnAfterHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken) => Task.CompletedTask;
                              }
                              """;

        var diagnostics = await AnalyzeAsync(source, new CommandInterceptorGuidanceAnalyzer());
        diagnostics.Should().NotContain(d => d.Id == "CQRA017");
    }
}
