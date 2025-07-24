using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Text;

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

            // Generate DI registrations
            try
            {
                var sourceCode = GenerateRegistrations(compilation, candidateClasses!);
                spc.AddSource("CqrsGeneratedRegistrar.g.cs", SourceText.From(sourceCode, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("CQRGEN999", "Unhandled Exception in CqrsSourceGenerator",
                        "Unhandled exception: {0}", "CQRSharp.Generators", DiagnosticSeverity.Error, true),
                    Location.None, ex.ToString()));
            }

            // Generate the request dispatcher
            try
            {
                var dispatcherSource = GenerateDispatcher(compilation, candidateClasses!);
                spc.AddSource("GeneratedRequestDispatcher.g.cs", SourceText.From(dispatcherSource, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("CQRGEN997", "Unhandled Exception in DispatcherGenerator",
                        "Unhandled exception: {0}", "CQRSharp.Generators", DiagnosticSeverity.Error, true),
                    Location.None, ex.ToString()));
            }
        });
    }
}