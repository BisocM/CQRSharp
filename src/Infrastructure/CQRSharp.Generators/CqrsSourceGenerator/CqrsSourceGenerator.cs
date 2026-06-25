using System;
using System.Text;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators.CqrsSourceGenerator;

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
	                // Surface well-known-type resolution problems loudly instead of silently emitting an empty registry.
                ReportWellKnownTypeIssues(CqrsKnownSymbols.For(compilation), spc);

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

                // Emit assembly markers (one per handled request/notification) so analyzers can discover handlers
                // across referenced assemblies.
                var markersSourceCode = GenerateAssemblyMarkers(compilation, candidateClasses!);
                spc.AddSource("CqrsGeneratedAssemblyMarkers.g.cs", SourceText.From(markersSourceCode, Encoding.UTF8));

                // Report handlers that are dropped from generated registration solely for accessibility (CQRGEN006).
                ReportInaccessibleHandlers(compilation, candidateClasses!, spc);
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

    private static readonly DiagnosticDescriptor WellKnownTypeUnresolvedDiagnostic = new(
        "CQRGEN007",
        "CQRSharp well-known type could not be resolved",
        "The CQRSharp framework type for role '{0}' could not be resolved from the referenced CQRSharp assemblies. Source generation will be incomplete; ensure the CQRSharp package versions are consistent.",
        "CQRSharp.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor CoreNotReferencedDiagnostic = new(
        "CQRGEN008",
        "CQRSharp.Core is not referenced",
        "CQRSharp.Abstractions is referenced but CQRSharp.Core is not, so CQRSharp source generation is skipped. Reference CQRSharp.Core (or the CQRSharp meta-package) to enable it.",
        "CQRSharp.Generators",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    // Turns the former silent no-op (an empty registry when a well-known type fails to resolve) into a build signal.
    private static void ReportWellKnownTypeIssues(CqrsKnownSymbols known, SourceProductionContext context)
    {
        // Not a CQRSharp consumer at all: nothing to report.
        if (!known.AbstractionsPresent) return;

        // Abstractions referenced but not Core (a legitimate abstractions-only/analyzer-only setup): one info note.
        if (!known.CoreReferenced)
        {
            context.ReportDiagnostic(Diagnostic.Create(CoreNotReferencedDiagnostic, Location.None));
            return;
        }

        // Core referenced but a required role is missing: genuine drift or a CQRSharp version mismatch.
        foreach (var roleName in known.GetMissingRequiredRoleNames())
            context.ReportDiagnostic(Diagnostic.Create(WellKnownTypeUnresolvedDiagnostic, Location.None, roleName));
    }
}
