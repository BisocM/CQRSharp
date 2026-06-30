using System;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Gen = CQRSharp.Generators.CqrsSourceGenerator.CqrsSourceGenerator;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     Proves the incremental-generator caching contract that the records-pipeline refactor exists to provide:
///     after an edit that changes a handler's method body but not any CQRSharp-relevant shape, the generator's
///     output step is served from cache instead of being recomputed. With the previous
///     <c>CompilationProvider.Combine(Collect())</c> pipeline this could not hold — the Compilation flowed into the
///     output step, so every keystroke re-ran full generation.
/// </summary>
public sealed class IncrementalGeneratorCachingTests
{
    private const string SourceTemplate = @"
using System.Threading;
using System.Threading.Tasks;
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Models.Commands;

namespace CachingProbe;

public sealed class ProbeCommand : CommandBase;

public sealed class ProbeCommandHandler : ICommandHandler<ProbeCommand>
{
    public Task<CommandResult> Handle(ProbeCommand command, CancellationToken cancellationToken)
    {
        var marker = __VALUE__;
        return Task.FromResult(CommandResult.FromSuccess());
    }
}
";

    [Fact(DisplayName = "Incremental generator: a handler body-only edit serves the output from cache")]
    public void BodyOnlyEdit_CachesOutput()
    {
        // Two compilations identical except for a literal inside the handler method body (same line count, so the
        // type's declaration location is unchanged too).
        var compilation1 = CreateCompilation(SourceTemplate.Replace("__VALUE__", "1"));
        var compilation2 = CreateCompilation(SourceTemplate.Replace("__VALUE__", "2"));

        var driver = CSharpGeneratorDriver.Create(
            new[] { new Gen().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        // First run populates the cache.
        var driverAfterFirst = driver.RunGenerators(compilation1);
        var firstSources = GeneratedSources(driverAfterFirst);

        // Second run with the body-only edit should reuse cached output.
        var driverAfterSecond = driverAfterFirst.RunGenerators(compilation2);
        var secondRun = driverAfterSecond.GetRunResult().Results.Single();

        // Sanity: the generated code is identical across the edit.
        GeneratedSources(driverAfterSecond).Should().Equal(firstSources);

        // The point of the refactor: the output steps were cached, not recomputed.
        var reasons = secondRun.TrackedOutputSteps
            .SelectMany(kvp => kvp.Value)
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason)
            .ToList();

        reasons.Should().NotBeEmpty("the generator produces source outputs");
        reasons.Should().OnlyContain(
            reason => reason == IncrementalStepRunReason.Cached || reason == IncrementalStepRunReason.Unchanged,
            "a body-only edit must not re-run source generation");
    }

    [Fact(DisplayName = "Incremental generator: adding a new request does re-run generation")]
    public void AddingARequest_Invalidates()
    {
        // A control case: a change that DOES alter the CQRSharp shape must invalidate the cache, proving the test
        // above is actually detecting caching rather than always passing.
        var compilation1 = CreateCompilation(SourceTemplate.Replace("__VALUE__", "1"));
        var compilation2 = CreateCompilation(
            SourceTemplate.Replace("__VALUE__", "1") + "\nnamespace CachingProbe { public sealed class ExtraCommand : CommandBase; }");

        var driver = CSharpGeneratorDriver.Create(
            new[] { new Gen().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        var driverAfterFirst = driver.RunGenerators(compilation1);
        var driverAfterSecond = driverAfterFirst.RunGenerators(compilation2);
        var secondRun = driverAfterSecond.GetRunResult().Results.Single();

        var reasons = secondRun.TrackedOutputSteps
            .SelectMany(kvp => kvp.Value)
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason)
            .ToList();

        reasons.Should().Contain(
            reason => reason == IncrementalStepRunReason.Modified || reason == IncrementalStepRunReason.New,
            "adding a request changes the collected models, so generation must re-run");
    }

    private static string[] GeneratedSources(GeneratorDriver driver) =>
        driver.GetRunResult().Results
            .Single()
            .GeneratedSources
            .OrderBy(s => s.HintName, StringComparer.Ordinal)
            .Select(s => s.SourceText.ToString())
            .ToArray();

    private static CSharpCompilation CreateCompilation(string source)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        return CSharpCompilation.Create(
            "CachingProbeAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
