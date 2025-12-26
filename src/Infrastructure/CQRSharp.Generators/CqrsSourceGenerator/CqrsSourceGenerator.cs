using System;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators.Cqrs;

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

	        var generatorConfig = context.AnalyzerConfigOptionsProvider
	            .Select(static (provider, _) => GeneratorConfig.From(provider.GlobalOptions));

	        var compilationAndCandidates = context.CompilationProvider.Combine(candidateClassesProvider.Collect());
	        var compilationCandidatesAndConfig = compilationAndCandidates.Combine(generatorConfig);

	        context.RegisterSourceOutput(compilationCandidatesAndConfig, (spc, source) =>
	        {
	            var ((compilation, candidateClasses), config) = source;
	
	            try
	            {
	                var stableNotifications = CollectStableNotifications(compilation, candidateClasses!, spc);
	
	                // Generate the DI registration helper
	                var registrarSourceCode = GenerateRegistrations(compilation, candidateClasses!, stableNotifications, spc, config);
	                spc.AddSource("CqrsGeneratedRegistrar.g.cs", SourceText.From(registrarSourceCode, Encoding.UTF8));

                // Generate the one-call DI bootstrap (AddCqrs + AddGenerated)
                var bootstrapSourceCode = GenerateBootstrap();
                spc.AddSource("CqrsGeneratedBootstrap.g.cs", SourceText.From(bootstrapSourceCode, Encoding.UTF8));

                // Generate AOT-safe outbox notification JSON serialization (only for stable-name notifications).
                if (stableNotifications.Length > 0)
                {
                    var outboxSerializerSourceCode = GenerateOutboxNotificationSerializer(stableNotifications);
                    spc.AddSource("CqrsGeneratedOutboxNotificationSerializer.g.cs", SourceText.From(outboxSerializerSourceCode, Encoding.UTF8));
                }

                // Generate the AOT-safe dispatcher
                var dispatcherSourceCode = GenerateDispatcher(compilation, candidateClasses!);
                spc.AddSource("GeneratedRequestDispatcher.g.cs", SourceText.From(dispatcherSourceCode, Encoding.UTF8));

                // Generate the AOT-safe stream dispatcher
                var streamDispatcherSourceCode = GenerateStreamDispatcher(compilation, candidateClasses!);
                spc.AddSource("GeneratedStreamRequestDispatcher.g.cs", SourceText.From(streamDispatcherSourceCode, Encoding.UTF8));

                // Generate the AOT-safe notification dispatcher
                var notificationDispatcherSourceCode = GenerateNotificationDispatcher(compilation, candidateClasses!);
                spc.AddSource("GeneratedDirectNotificationDispatcher.g.cs",
                    SourceText.From(notificationDispatcherSourceCode, Encoding.UTF8));

                // Generate the diagnostics/introspection API (request bindings)
                var diagnosticsSourceCode = GenerateDiagnostics(compilation, candidateClasses!);
                spc.AddSource("GeneratedCqrsDiagnostics.g.cs", SourceText.From(diagnosticsSourceCode, Encoding.UTF8));
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
