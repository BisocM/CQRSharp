using System;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators.Types;

[Generator]
public sealed partial class CqrsSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidateClassesProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) as INamedTypeSymbol)
            .Where(symbol => symbol is not null);

        var compilationAndCandidates = context.CompilationProvider.Combine(candidateClassesProvider.Collect());

        context.RegisterSourceOutput(compilationAndCandidates, (spc, source) =>
        {
            var (compilation, candidateClasses) = source;

            try
            {
                // Generate the DI registration helper
                var registrarSourceCode = GenerateRegistrations(compilation, candidateClasses!);
                spc.AddSource("CqrsGeneratedRegistrar.g.cs", SourceText.From(registrarSourceCode, Encoding.UTF8));

                // Generate the AOT-safe dispatcher
                var dispatcherSourceCode = GenerateDispatcher(compilation, candidateClasses!);
                spc.AddSource("GeneratedRequestDispatcher.g.cs", SourceText.From(dispatcherSourceCode, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("CQRGEN999", "Unhandled Exception in CqrsSourceGenerator",
                        "Unhandled exception: {0}", "CQRSharp.Generators", DiagnosticSeverity.Error, true),
                    Location.None, ex.ToString()));
            }
        });
    }
}