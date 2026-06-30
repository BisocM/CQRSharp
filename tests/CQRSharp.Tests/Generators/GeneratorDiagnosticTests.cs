using System;
using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Gen = CQRSharp.Generators.CqrsSourceGenerator.CqrsSourceGenerator;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     Diagnostics the source generator reports (CQRGEN*). Driven through <see cref="CSharpGeneratorDriver" /> so the
///     exact reporting path is exercised.
/// </summary>
public sealed class GeneratorDiagnosticTests
{
    private const string OpenGenericHandlerSource = @"
using System.Threading;
using System.Threading.Tasks;
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Models.Commands;

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
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Models.Commands;

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

    private static ImmutableArray<Diagnostic> RunGenerator(string source)
    {
        var driver = CSharpGeneratorDriver.Create(new Gen().AsSourceGenerator());
        return driver.RunGenerators(CreateCompilation(source)).GetRunResult().Diagnostics;
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        return CSharpCompilation.Create(
            "Cqrgen009ProbeAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
