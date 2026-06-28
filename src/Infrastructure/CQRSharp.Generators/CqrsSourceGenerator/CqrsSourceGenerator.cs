using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators.CqrsSourceGenerator;

[Generator]
public sealed partial class CqrsSourceGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor WellKnownTypeUnresolvedDiagnostic = new(
        "CQRGEN007",
        "CQRSharp well-known type could not be resolved",
        "The CQRSharp framework type for role '{0}' could not be resolved from the referenced CQRSharp assemblies. Source generation will be incomplete; ensure the CQRSharp package versions are consistent.",
        "CQRSharp.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor CoreNotReferencedDiagnostic = new(
        "CQRGEN008",
        "CQRSharp.Core is not referenced",
        "CQRSharp.Abstractions is referenced but CQRSharp.Core is not, so CQRSharp source generation is skipped. Reference CQRSharp.Core (or the CQRSharp meta-package) to enable it.",
        "CQRSharp.Generators",
        DiagnosticSeverity.Info,
        true);

    private static readonly DiagnosticDescriptor DuplicateNotificationNameDiagnostic = new(
        "CQRGEN002",
        "Duplicate NotificationName",
        "Duplicate [NotificationName] '{0}' found on: {1}. Stable names must be unique for outbox serialization.",
        "CQRSharp.Generators",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Project each candidate type into a small, value-equatable model in the transform. Because no symbols or
        // Compilation flow past this point, an edit that doesn't change a type's CQRSharp shape yields identical
        // models and the whole output step short-circuits (and nothing symbol-bearing is held across generations).
        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, ct) => TransformCandidate(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

        // Compilation-level facts the per-candidate transform can't see (Core referenced? Pipelines builder present?),
        // resolved once into an equatable snapshot so this branch only retriggers when those facts actually change.
        var knownSnapshot = context.CompilationProvider.Select(static (compilation, _) => CreateKnownSnapshot(compilation));

        var config = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) => GeneratorConfig.From(provider.GlobalOptions));

        var combined = candidates.Combine(knownSnapshot).Combine(config);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var ((models, known), config) = source;
            Execute(spc, models, known, config);
        });
    }

    private static void Execute(
        SourceProductionContext context,
        ImmutableArray<CandidateModel> candidates,
        KnownSnapshot known,
        GeneratorConfig config)
    {
        try
        {
            // Surface well-known-type resolution problems loudly instead of silently emitting an empty registry.
            ReportWellKnownTypeIssues(known, context);

            var stableNotifications = CollectSerializableNotifications(candidates, context);

            var registrarSourceCode = GenerateRegistrations(candidates, known, stableNotifications, context, config);
            context.AddSource("CqrsGeneratedRegistrar.g.cs", SourceText.From(registrarSourceCode, Encoding.UTF8));

            var bootstrapSourceCode = GenerateBootstrap(known);
            context.AddSource("CqrsGeneratedBootstrap.g.cs", SourceText.From(bootstrapSourceCode, Encoding.UTF8));

            if (stableNotifications.Count > 0)
            {
                var outboxSerializerSourceCode = GenerateOutboxNotificationSerializer(stableNotifications);
                context.AddSource("CqrsGeneratedOutboxNotificationSerializer.g.cs", SourceText.From(outboxSerializerSourceCode, Encoding.UTF8));
            }

            var dispatcherSourceCode = GenerateDispatcher(candidates);
            context.AddSource("GeneratedRequestDispatcher.g.cs", SourceText.From(dispatcherSourceCode, Encoding.UTF8));

            var streamDispatcherSourceCode = GenerateStreamDispatcher(candidates);
            context.AddSource("GeneratedStreamRequestDispatcher.g.cs", SourceText.From(streamDispatcherSourceCode, Encoding.UTF8));

            var notificationDispatcherSourceCode = GenerateNotificationDispatcher(candidates);
            context.AddSource("GeneratedDirectNotificationDispatcher.g.cs", SourceText.From(notificationDispatcherSourceCode, Encoding.UTF8));

            var diagnosticsSourceCode = GenerateDiagnostics(candidates);
            context.AddSource("GeneratedCqrsDiagnostics.g.cs", SourceText.From(diagnosticsSourceCode, Encoding.UTF8));

            var notificationRegistrySourceCode = GenerateNotificationRegistry(candidates, stableNotifications);
            context.AddSource("GeneratedCqrsNotificationRegistry.g.cs", SourceText.From(notificationRegistrySourceCode, Encoding.UTF8));

            var markersSourceCode = GenerateAssemblyMarkers(candidates);
            context.AddSource("CqrsGeneratedAssemblyMarkers.g.cs", SourceText.From(markersSourceCode, Encoding.UTF8));

            ReportInaccessibleHandlers(candidates, context);
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor("CQRGEN999", "Unhandled Exception in CqrsSourceGenerator",
                    "Unhandled exception: {0}", "CQRSharp.Generators", DiagnosticSeverity.Error, true),
                Location.None, ex.ToString()));
        }
    }

    // Aggregates notification models across candidates: reports CQRGEN005 (unserializable stable-named notifications)
    // and CQRGEN002 (duplicate stable names), then returns the deduped, ordered serializable set.
    private static IReadOnlyList<NotificationModel> CollectSerializableNotifications(
        ImmutableArray<CandidateModel> candidates,
        SourceProductionContext context)
    {
        var notifications = candidates
            .Where(c => c.Notification is not null)
            .Select(c => c.Notification!)
            .ToArray();

        foreach (var notification in notifications.Where(n => n.StableName is not null && n.OutboxError is not null))
        {
            var location = notification.OutboxErrorLocation?.ToLocation() ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                OutboxNotificationNotAotJsonSerializableDiagnostic,
                location,
                notification.TypeName,
                notification.OutboxError));
        }

        var stable = notifications
            .Where(n => n.StableName is not null && n.OutboxRoot is not null)
            .ToArray();

        foreach (var group in stable
                     .GroupBy(n => n.StableName, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            var types = string.Join(", ", group.Select(n => n.TypeName));
            context.ReportDiagnostic(Diagnostic.Create(DuplicateNotificationNameDiagnostic, Location.None, group.Key!, types));
        }

        return stable
            .GroupBy(n => n.StableName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(n => n.StableName, StringComparer.Ordinal)
            .ThenBy(n => n.TypeName, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ReportWellKnownTypeIssues(KnownSnapshot known, SourceProductionContext context)
    {
        if (!known.AbstractionsPresent) return;

        if (!known.CoreReferenced)
        {
            context.ReportDiagnostic(Diagnostic.Create(CoreNotReferencedDiagnostic, Location.None));
            return;
        }

        foreach (var roleName in known.MissingRequiredRoleNames)
            context.ReportDiagnostic(Diagnostic.Create(WellKnownTypeUnresolvedDiagnostic, Location.None, roleName));
    }

    private static void ReportInaccessibleHandlers(ImmutableArray<CandidateModel> candidates, SourceProductionContext context)
    {
        foreach (var candidate in candidates.Where(c => c is { IsConcrete: true, IsAccessible: false, ImplementsAnyKnownHandlerInterface: true }))
        {
            var location = candidate.Location?.ToLocation() ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(HandlerNotAccessibleDiagnostic, location, candidate.TypeName));
        }
    }
}
