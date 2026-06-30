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

    private static readonly DiagnosticDescriptor OpenGenericHandlerDiagnostic = new(
        "CQRGEN009",
        "Open-generic handler is not registered",
        "'{0}' is an open-generic handler, which CQRSharp does not register — only closed, non-generic handler types are wired. Declare a concrete (closed) handler or register it manually; otherwise dispatching its request throws \"no handler\" at runtime.",
        "CQRSharp.Generators",
        DiagnosticSeverity.Warning,
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
            var hasModule = HasModuleContent(candidates);

            // Every assembly with registerable content emits its own uniquely-namespaced module (dispatchers,
            // registry data, registrar) — never colliding across assemblies in one reference graph.
            if (hasModule)
            {
                context.AddSource("CqrsModule.g.cs",
                    SourceText.From(GenerateModule(candidates, known, stableNotifications, context, config), Encoding.UTF8));
                context.AddSource("GeneratedRequestDispatcher.g.cs",
                    SourceText.From(GenerateDispatcher(candidates, known), Encoding.UTF8));
                context.AddSource("GeneratedStreamRequestDispatcher.g.cs",
                    SourceText.From(GenerateStreamDispatcher(candidates, known), Encoding.UTF8));
                context.AddSource("GeneratedDirectNotificationDispatcher.g.cs",
                    SourceText.From(GenerateNotificationDispatcher(candidates, known), Encoding.UTF8));
                context.AddSource("GeneratedCqrsDiagnostics.g.cs",
                    SourceText.From(GenerateDiagnostics(candidates, known), Encoding.UTF8));

                if (stableNotifications.Count > 0)
                    context.AddSource("GeneratedOutboxNotificationSerializer.g.cs",
                        SourceText.From(GenerateOutboxNotificationSerializer(stableNotifications, known), Encoding.UTF8));
            }

            // Every CQRSharp-referencing assembly emits the internal AddGenerated/AddCqrsGenerated entry points (which
            // wire this assembly's module plus every referenced assembly's module). Internal visibility means they
            // never collide across assemblies, so there is no "composition root" to designate — you call
            // AddCqrsGenerated from wherever you set up DI, and it wires that assembly's whole reference graph.
            if (known.CoreReferenced)
                context.AddSource("CqrsGeneratedBootstrap.g.cs",
                    SourceText.From(GenerateBootstrap(known, hasModule), Encoding.UTF8));

            var markersSourceCode = GenerateAssemblyMarkers(candidates, known, hasModule);
            context.AddSource("CqrsGeneratedAssemblyMarkers.g.cs", SourceText.From(markersSourceCode, Encoding.UTF8));

            ReportInaccessibleHandlers(candidates, context);
            ReportOpenGenericHandlers(candidates, context);
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

    // CQRGEN009: an open-generic dispatch handler is silently unregistered (the generator wires only closed types),
    // surfacing only as a runtime "no handler". Flag it at the declaration so the gap is caught at build time.
    private static void ReportOpenGenericHandlers(ImmutableArray<CandidateModel> candidates, SourceProductionContext context)
    {
        foreach (var candidate in candidates.Where(c => c.IsOpenGenericHandler))
        {
            var location = candidate.Location?.ToLocation() ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(OpenGenericHandlerDiagnostic, location, candidate.TypeName));
        }
    }

    // Emits an XML <summary> doc comment for a member of the generated code, at the given indentation.
    private static void EmitSummary(StringBuilder sb, string indent, string text)
    {
        sb.AppendLine($"{indent}/// <summary>");
        sb.AppendLine($"{indent}///     {text}");
        sb.AppendLine($"{indent}/// </summary>");
    }
}
