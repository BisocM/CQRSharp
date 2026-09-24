using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Gen = CQRSharp.Generators.CqrsSourceGenerator.CqrsSourceGenerator;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     The in-memory compilations the generator and analyzer tests run. Every run fails the test outright when an analyzer
///     or the generator crashes: a crash replaces the component's diagnostics with AD0001 / CQRGEN999, so a test that
///     asserts a diagnostic is absent would otherwise pass on a component that threw.
/// </summary>
internal static class CompilationHarness
{
    /// <summary>
    ///     Compiles <paramref name="sources" /> (nullable enabled, one tree per source named <c>Probe{i}.cs</c>) and throws
    ///     when the input itself does not compile, so a test cannot silently run on a broken snippet.
    /// </summary>
    public static CSharpCompilation Compile(
        IEnumerable<string> sources,
        string assemblyName = "Probe",
        IEnumerable<MetadataReference>? references = null)
    {
        var compilation = CreateCompilation(sources, assemblyName, references);
        var errors = Errors(compilation);
        if (errors.Length > 0)
            throw new InvalidOperationException("The test input does not compile: " + string.Join(" | ", errors));
        return compilation;
    }

    /// <summary>Compiles <paramref name="source" /> and runs <paramref name="analyzers" /> over it.</summary>
    public static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, params DiagnosticAnalyzer[] analyzers)
        => AnalyzeAsync(Compile([source]), analyzers);

    /// <summary>
    ///     Runs the CQRSharp source generator over <paramref name="source" />, then <paramref name="analyzers" /> over the result,
    ///     as a build does: the analyzers see the generated module, markers and bootstrap.
    /// </summary>
    public static Task<ImmutableArray<Diagnostic>> AnalyzeWithGeneratorsAsync(string source, params DiagnosticAnalyzer[] analyzers)
    {
        var run = RunGenerators([source]);
        if (run.CompileErrors.Length > 0)
            throw new InvalidOperationException("The test input does not compile with the generated code: " + string.Join(" | ", run.CompileErrors));
        return AnalyzeAsync(run.Output, analyzers);
    }

    /// <summary>Runs <paramref name="analyzers" /> over <paramref name="compilation" /> and fails on an analyzer crash.</summary>
    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Compilation compilation, params DiagnosticAnalyzer[] analyzers)
    {
        var diagnostics = await compilation.WithAnalyzers(analyzers.ToImmutableArray()).GetAnalyzerDiagnosticsAsync();
        var crashes = diagnostics.Where(d => d.Id is "AD0001" or "AD0002").ToArray();
        if (crashes.Length > 0)
            throw new InvalidOperationException("An analyzer crashed: " + string.Join(" | ", crashes.Select(d => d.GetMessage())));
        return diagnostics;
    }

    /// <summary>
    ///     Runs the CQRSharp source generator over <paramref name="sources" /> and returns what it reported and emitted,
    ///     plus the errors of the resulting compilation. The input may use generated code (AddCqrsGenerated), so it is not
    ///     required to compile on its own; a generator crash fails the test.
    /// </summary>
    public static GeneratorRun RunGenerators(
        IEnumerable<string> sources,
        IEnumerable<MetadataReference>? extraReferences = null,
        string assemblyName = "GeneratorProbe",
        IEnumerable<MetadataReference>? references = null)
    {
        var compilation = CreateCompilation(
            sources, assemblyName, (references ?? ProbeReferences.Create()).Concat(extraReferences ?? []));

        var driver = CSharpGeneratorDriver
            .Create(new Gen().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var driverDiagnostics);

        var result = driver.GetRunResult();
        var crashes = driverDiagnostics.Concat(result.Diagnostics)
            .Where(d => d.Id is "CQRGEN999" or "CS8784" or "CS8785")
            .Select(d => d.GetMessage())
            .Concat(result.Results.Where(r => r.Exception is not null).Select(r => r.Exception!.ToString()))
            .ToArray();
        if (crashes.Length > 0)
            throw new InvalidOperationException("A generator crashed: " + string.Join(" | ", crashes));

        var generated = result.Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString(), StringComparer.Ordinal);

        var errors = Errors(output);
        MetadataReference? reference = null;
        if (errors.Length == 0)
        {
            using var stream = new MemoryStream();
            if (output.Emit(stream).Success) reference = MetadataReference.CreateFromImage(stream.ToArray());
        }

        return new GeneratorRun(result.Diagnostics, errors, generated, output, reference);
    }

    /// <summary>
    ///     Compiles <paramref name="sources" /> without checking them: for input that only compiles once the generated code
    ///     is added (a plain <c>AddCqrsGenerated()</c> call), which its test checks after running the generator.
    /// </summary>
    public static CSharpCompilation CreateCompilation(IEnumerable<string> sources, string assemblyName, IEnumerable<MetadataReference>? references = null)
        => CSharpCompilation.Create(
            assemblyName,
            sources.Select((source, i) => CSharpSyntaxTree.ParseText(source, path: $"Probe{i}.cs")),
            references ?? ProbeReferences.Create(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>The errors of <paramref name="compilation" />, one line each.</summary>
    public static string[] Errors(Compilation compilation)
        => compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} {d.Location.SourceTree?.FilePath}: {d.GetMessage()}")
            .ToArray();
}

/// <summary>What one generator run reported and emitted.</summary>
/// <param name="GeneratorDiagnostics">The diagnostics the generators reported (CQRGEN*).</param>
/// <param name="CompileErrors">The errors of the compilation with the generated code added.</param>
/// <param name="Sources">The generated sources by hint name.</param>
/// <param name="Output">The compilation with the generated code added.</param>
/// <param name="Reference">That compilation as a reference, when it has no errors.</param>
internal sealed record GeneratorRun(
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    string[] CompileErrors,
    IReadOnlyDictionary<string, string> Sources,
    Compilation Output,
    MetadataReference? Reference)
{
    /// <summary>The generated source with the given hint name; fails the test when it was not generated.</summary>
    public string Generated(string hintName)
        => Sources.TryGetValue(hintName, out var text)
            ? text
            : throw new InvalidOperationException($"'{hintName}' was not generated. Generated: {string.Join(", ", Sources.Keys)}");
}
