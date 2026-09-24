using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators.CqrsSourceGenerator;

[Generator]
public sealed partial class CqrsSourceGenerator : IIncrementalGenerator
{
    /// <summary>The names of the pipeline's steps, which the caching tests assert on.</summary>
    private static class TrackingNames
    {
        public const string Candidates = "CqrsCandidates";
        public const string KnownSnapshot = "CqrsKnownSnapshot";
        public const string GenerationModels = "CqrsGenerationModels";
        public const string GenerationInput = "CqrsGenerationInput";
        public const string BootstrapCallSites = "CqrsBootstrapCallSites";
        public const string DiagnosticsInput = "CqrsDiagnosticsInput";
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The semantic transform runs for every class, struct and record on each edit; what it yields is a small
        // value-equatable model with no symbol or Compilation in it, so everything after it is skipped when the models
        // compare equal. Structs take part for the notifications they declare.
        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, ct) => TransformCandidate(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .WithTrackingName(TrackingNames.Candidates);

        // Compilation-level facts the per-candidate transform can't see (Core referenced? Pipelines builder present? the
        // referenced modules?). Recomputed for every compilation; the steps after it re-run only when they change.
        var knownSnapshot = context.CompilationProvider
            .Select(static (compilation, _) => CreateKnownSnapshot(compilation))
            .WithTrackingName(TrackingNames.KnownSnapshot);

        // Code generation reads the models without their source locations: an edit that only moves a CQRSharp type
        // (a line added above it) leaves the generated code as it is and does not re-run this output.
        var generationInput = candidates
            .Select(static (model, _) => model.WithoutLocations())
            .WithTrackingName(TrackingNames.GenerationModels)
            .Collect()
            .Combine(knownSnapshot)
            .WithTrackingName(TrackingNames.GenerationInput);

        context.RegisterSourceOutput(generationInput, static (spc, source) => Generate(spc, source.Left, source.Right));

        // Where this assembly calls the generated entry points, which is where it composes its reference graph: the call
        // sites CQRGEN019 marks when that composition is ambiguous, and, among them, the ones by plain name that CQRGEN015
        // marks when a referenced assembly's copy is visible here (a call qualified with the bootstrap type is already
        // unambiguous).
        var bootstrapCallSites = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsBootstrapCallShape(node),
                static (ctx, ct) => ClassifyBootstrapCall(ctx, ct) is { } isPlain && LocationInfo.CreateFrom(ctx.Node) is { } location
                    ? new BootstrapCallSiteModel(location, isPlain)
                    : null)
            .Where(static site => site is not null)
            .Select(static (site, _) => site!)
            .Collect()
            .WithTrackingName(TrackingNames.BootstrapCallSites);

        // Diagnostics read the models with their locations, so they are reported where the code is.
        var diagnosticsInput = candidates
            .Collect()
            .Combine(knownSnapshot)
            .Combine(bootstrapCallSites)
            .WithTrackingName(TrackingNames.DiagnosticsInput);

        context.RegisterSourceOutput(diagnosticsInput, static (spc, source) => ReportDiagnostics(spc, source.Left.Left, source.Left.Right, source.Right));
    }

    private static bool IsBootstrapCallShape(SyntaxNode node)
        => node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } &&
           access.Name.Identifier.Text == "AddCqrsGenerated";

    // Whether a call named AddCqrsGenerated calls the generated bootstrap: true for a plain call, an extension-method call
    // on a service collection (services.AddCqrsGenerated()); false for one through the bootstrap type, by its name or an
    // alias (CqrsGeneratedBootstrap.AddCqrsGenerated(services)); null for anything else. Bound semantically where it can
    // be, and by name where it cannot: this assembly's own bootstrap is not in the compilation the syntax provider sees,
    // so a call through it does not bind.
    private static bool? ClassifyBootstrapCall(GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var access = (MemberAccessExpressionSyntax)((InvocationExpressionSyntax)ctx.Node).Expression;
        var model = ctx.SemanticModel;

        if (model.GetAliasInfo(access.Expression, ct) is { Target: var aliased })
            return aliased is INamedTypeSymbol { Name: CqrsKnownSymbols.BootstrapTypeName } ? false : null;

        var qualifier = model.GetSymbolInfo(access.Expression, ct);
        switch (qualifier.Symbol ?? qualifier.CandidateSymbols.FirstOrDefault())
        {
            case ITypeSymbol type:
                return type.Name == CqrsKnownSymbols.BootstrapTypeName ? false : null;
            case INamespaceSymbol:
                return null;
        }

        if (RightmostName(access.Expression) == CqrsKnownSymbols.BootstrapTypeName) return false;

        var serviceCollection = model.Compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.IServiceCollection");
        var receiver = model.GetTypeInfo(access.Expression, ct).Type;
        return serviceCollection is not null && receiver is not null &&
               (SymbolEqualityComparer.Default.Equals(receiver, serviceCollection) ||
                receiver.AllInterfaces.Contains(serviceCollection, SymbolEqualityComparer.Default))
            ? true
            : null;

        static string? RightmostName(ExpressionSyntax expression)
            => expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                _ => null
            };
    }

    // Emits the generated sources. Reports nothing but a crash: every other diagnostic comes from ReportDiagnostics, which
    // reads the same models through the same helpers, so what is reported and what is emitted agree.
    private static void Generate(SourceProductionContext context, ImmutableArray<CandidateModel> candidates, KnownSnapshot known)
    {
        try
        {
            // Every generated module implements CQRSharp.Core's ICqrsModule, so without Core there is nothing to emit;
            // code emitted against a missing framework type would only bury CQRGEN007 under compile errors.
            if (!known.CoreReferenced || known.MissingRequiredTypeNames.Count > 0) return;

            var suppressed = GeneratedFileSuppressions(candidates);
            var stableNotifications = SerializableNotifications(candidates);
            var fingerprints = Fingerprints(candidates).Where(f => f.Root is not null).ToArray();
            var hasModule = HasModuleContent(candidates);

            // Every assembly with registerable content emits its own uniquely-namespaced module (its tables and its
            // registrar), never colliding across assemblies in one reference graph.
            if (hasModule)
            {
                context.AddSource("CqrsModule.g.cs",
                    SourceText.From(GenerateModule(candidates, known, stableNotifications, fingerprints.Length > 0, suppressed), Encoding.UTF8));

                if (stableNotifications.Count > 0)
                    context.AddSource("GeneratedOutboxNotificationSerializer.g.cs",
                        SourceText.From(GenerateOutboxNotificationSerializer(stableNotifications, known, suppressed), Encoding.UTF8));

                if (fingerprints.Length > 0)
                    context.AddSource("GeneratedRequestFingerprinter.g.cs",
                        SourceText.From(GenerateRequestFingerprinter(fingerprints, known, suppressed), Encoding.UTF8));
            }

            // Every CQRSharp.Core-referencing assembly emits the internal AddCqrsGenerated entry points (which wire
            // this assembly's module plus every referenced assembly's module). Internal visibility means they never
            // collide across assemblies, so there is no "composition root" to designate — you call
            // AddCqrsGenerated from wherever you set up DI, and it wires that assembly's whole reference graph.
            context.AddSource("CqrsGeneratedBootstrap.g.cs",
                SourceText.From(GenerateBootstrap(known, hasModule), Encoding.UTF8));

            context.AddSource("CqrsGeneratedAssemblyMarkers.g.cs",
                SourceText.From(GenerateAssemblyMarkers(candidates, known, hasModule, suppressed), Encoding.UTF8));
        }
        catch (Exception ex)
        {
            ReportCrash(context, ex);
        }
    }

    // Whether this compilation has anything to register, i.e. whether a per-assembly module should be emitted.
    private static bool HasModuleContent(ImmutableArray<CandidateModel> candidates)
    {
        foreach (var candidate in candidates)
            if (candidate.Handlers.Count > 0 ||
                candidate.HandlerForwarderInterfaces.Count > 0 ||
                candidate.DiscoveredServiceInterfaces.Count > 0 ||
                candidate.Request is not null ||
                candidate.Notification is not null ||
                candidate.HandledNotifications.Count > 0 ||
                candidate.ContextFactories.Count > 0 ||
                candidate.ExceptionHooks.Count > 0 ||
                candidate.NotificationClosedBehaviors.Count > 0 ||
                candidate.NotificationClosedBehaviorGaps.Count > 0)
                return true;

        return false;
    }

    // The request bindings of every handler, by request type: one entry per request, which SelectDeterministicBinding
    // then narrows to one binding when several handlers claim it (CQRGEN004).
    private static Dictionary<string, List<HandlerImplModel>> HandlerBindingsByRequest(ImmutableArray<CandidateModel> candidates)
        => candidates
            .SelectMany(c => c.Handlers)
            .GroupBy(h => h.Request.RequestTypeName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    private static HandlerImplModel SelectDeterministicBinding(List<HandlerImplModel> bindings)
        => bindings
            .OrderBy(b => b.ImplTypeName, StringComparer.Ordinal)
            .ThenBy(b => b.InterfaceNameOrdinal, StringComparer.Ordinal)
            .First();

    // The stable-named notifications the outbox serializer covers: one per stable name (CQRGEN002 reports a clash),
    // ordered by name, leaving out the ones whose shape cannot be serialized (CQRGEN005).
    private static IReadOnlyList<NotificationModel> SerializableNotifications(ImmutableArray<CandidateModel> candidates)
        => candidates
            .Where(c => c.Notification is { StableName: not null, OutboxRoot: not null })
            .Select(c => c.Notification!)
            .GroupBy(n => n.StableName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(n => n.StableName, StringComparer.Ordinal)
            .ThenBy(n => n.TypeName, StringComparer.Ordinal)
            .ToArray();

    // The idempotent requests this module fingerprints, each with the candidate it was read from: the requests declared
    // here, and the requests handled here that no other module fingerprints. One per request type; those whose payload
    // cannot be rendered carry the reason instead of a graph (CQRGEN014).
    private static IReadOnlyList<FingerprintModel> Fingerprints(ImmutableArray<CandidateModel> candidates)
        => FingerprintsWithCandidates(candidates).Select(f => f.Model).OrderBy(m => m.TypeName, StringComparer.Ordinal).ToArray();

    private static (CandidateModel Candidate, FingerprintModel Model)[] FingerprintsWithCandidates(ImmutableArray<CandidateModel> candidates)
        => candidates
            .Where(c => c.Fingerprint is not null)
            .Select(c => (Candidate: c, Model: c.Fingerprint!))
            .Concat(candidates.SelectMany(c => c.HandledRequestFingerprints.Select(f => (Candidate: c, Model: f))))
            .GroupBy(f => f.Model.TypeName, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();

    // Emits an XML <summary> doc comment for a member of the generated code, at the given indentation.
    private static void EmitSummary(StringBuilder sb, string indent, string text)
    {
        sb.AppendLine($"{indent}/// <summary>");
        sb.AppendLine($"{indent}///     {text}");
        sb.AppendLine($"{indent}/// </summary>");
    }
}
