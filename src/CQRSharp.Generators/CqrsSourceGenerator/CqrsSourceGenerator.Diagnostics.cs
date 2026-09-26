using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    private const string Category = "CQRSharp.Generators";

    // ============================================================================================================
    // Descriptors. Errors first, then warnings, then notes.
    // ============================================================================================================

    private static readonly DiagnosticDescriptor DuplicateNotificationNameDiagnostic = new(
        "CQRGEN002",
        "Duplicate NotificationName",
        "Duplicate [NotificationName] '{0}' found on: {1}. Stable names must be unique for outbox serialization.",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor MultipleRequestHandlersDiagnostic = new(
        "CQRGEN004",
        "Multiple handlers found for request",
        "Multiple handlers found for request '{0}': {1}. CQRSharp requires exactly one handler per request.",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor OutboxNotificationNotSerializableDiagnostic = new(
        "CQRGEN005",
        "Outbox notification cannot be serialized",
        "Notification '{0}' cannot be serialized by the generated outbox serializer: {1}. Change the notification's shape, or remove [NotificationName] and register a serializer of your own with AddNotificationSerializer<T>(), which replaces the generated one and must then name and serialize every durable notification.",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor FrameworkTypeUnresolvedDiagnostic = new(
        "CQRGEN007",
        "CQRSharp framework type could not be resolved",
        "The CQRSharp framework type '{0}' could not be resolved although CQRSharp.Core is referenced, so source generation is skipped. Reference the same version of every CQRSharp package.",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor PartitionByPropertyNotFoundDiagnostic = new(
        "CQRGEN011",
        "PartitionBy names no readable property",
        "Notification '{0}' declares [NotificationName(PartitionBy = \"{1}\")], but it has no readable instance property named '{1}' that generated code can see, so its outbox deliveries cannot be ordered. Name a public or internal property, or implement IPartitionedNotification.",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor DuplicateNotificationHandlerNameDiagnostic = new(
        "CQRGEN012",
        "Duplicate notification handler name",
        "The notification handler name '{0}' is used by more than one handler: {1}. Outbox messages are addressed to a handler by its name, so each handler needs a unique [NotificationHandlerName].",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor RequestAttributeNotReproducibleDiagnostic = new(
        "CQRGEN016",
        "Request attribute cannot be applied by generated code",
        "'{0}' is dispatched without {1}: generated code cannot rebuild the attribute. Make the types it names public or internal (to this assembly, or to one that grants it InternalsVisibleTo).",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor ResponseTypeMismatchDiagnostic = new(
        "CQRGEN017",
        "Handler's response type is not the one its request is dispatched with",
        "'{0}' implements '{1}', declared over the response type '{2}', but '{3}' is dispatched with '{4}', so the dispatcher never calls it. Declare it over '{4}'.",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor DuplicateContextFactoryDiagnostic = new(
        "CQRGEN018",
        "More than one context factory for a context type",
        "Context type '{0}' has more than one IRequestContextFactory in this assembly: {1}. A context type has one factory, so all but one would be silently replaced. Keep one.",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor MissingRequestHandlerDiagnostic = new(
        "CQRGEN003",
        "No handler found for request",
        "No handler found for request '{0}'. Add exactly one handler, or suppress CQRGEN003 in a project that only declares requests whose handlers live elsewhere.",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor HandlerNotAccessibleDiagnostic = new(
        "CQRGEN006",
        "Handler cannot be named by generated registration",
        "'{0}' implements a CQRSharp handler interface but generated code cannot name it (it is less accessible than internal, or marked [Obsolete] as an error), so it is skipped by generated registration. Make it public or internal (and not nested in a less-accessible type) without an error-level [Obsolete], or register it manually.",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor OpenGenericHandlerDiagnostic = new(
        "CQRGEN009",
        "Open-generic handler is not registered",
        "'{0}' is an open-generic handler, which CQRSharp does not register — only closed, non-generic handler types are wired. Declare a concrete (closed) handler or register it manually; otherwise dispatching its request throws \"no handler\" at runtime.",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor InaccessibleBoundTypeDiagnostic = new(
        "CQRGEN010",
        "Binding skipped: generated code cannot name a bound type",
        "'{0}' is not wired by generated registration for '{1}': generated code cannot name '{1}' (it is less accessible than internal, internal to another assembly, or marked [Obsolete] as an error), so that request is not routed and that binding never runs. Make '{1}' public or internal, without an error-level [Obsolete].",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor BlankNotificationHandlerNameDiagnostic = new(
        "CQRGEN013",
        "Blank notification handler name",
        "'{0}' carries a [NotificationHandlerName] whose name is empty or whitespace; the handler's type name is used instead. Give it a stable, non-empty name or remove the attribute.",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor BootstrapVisibilityDiagnostic = new(
        "CQRGEN015",
        "A referenced assembly's AddCqrsGenerated is visible here",
        "A referenced assembly exposes its internal AddCqrsGenerated to this one (InternalsVisibleTo), so a plain AddCqrsGenerated() call here is ambiguous, or binds to that assembly's copy and never registers this assembly's own module. Call '{0}' instead (or add 'using {1};' inside a namespace block).",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor AmbiguousContextFactoryDiagnostic = new(
        "CQRGEN019",
        "Context factories for one context type in more than one referenced assembly",
        "Context type '{0}' has an IRequestContextFactory in more than one referenced assembly ({1}) and none in this assembly, which composes them, so the order their modules are registered in decides: '{2}' creates its contexts. Declare an IRequestContextFactory<{0}> in this assembly, which replaces theirs, or keep only one of them. A factory registered by hand replaces them too; this diagnostic cannot see one, so suppress it then.",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor ComposedNotificationNameClashDiagnostic = new(
        "CQRGEN020",
        "One notification name in more than one composed assembly",
        "The notification name '{0}' is given to more than one notification type in the assemblies this one composes ({1}). While the outbox is on, a message stored under it could not be read back as the type it was stored as, so '{2}' keeps the name and publishing any other of them through the outbox fails with CQRCONF010. Give each a unique [NotificationName].",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor ComposedHandlerNameClashDiagnostic = new(
        "CQRGEN021",
        "One notification handler name in more than one composed assembly",
        "The notification handler name '{0}' is used by more than one handler in the assemblies this one composes ({1}). Outbox messages are addressed to a handler by its name, so while the outbox is on a message for it is ambiguous (CQRCONF009). Give each handler a unique [NotificationHandlerName].",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor RequestNotFingerprintableDiagnostic = new(
        "CQRGEN014",
        "Idempotent request cannot be fingerprinted automatically",
        "Idempotent request '{0}' cannot be fingerprinted automatically: {1}. Without a fingerprint an idempotency key reused with a different payload is not detected; implement IFingerprintedRequest to supply one, or leave properties that are not payload out of it with an unconditional [JsonIgnore].",
        Category,
        DiagnosticSeverity.Info,
        true);

    private static readonly DiagnosticDescriptor GeneratorCrashDiagnostic = new(
        "CQRGEN999",
        "Unhandled Exception in CqrsSourceGenerator",
        "Unhandled exception: {0}",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static void ReportCrash(SourceProductionContext context, Exception exception)
        => context.ReportDiagnostic(Diagnostic.Create(GeneratorCrashDiagnostic, Location.None, exception.ToString()));

    // ============================================================================================================
    // Reporting. Reads the models with their locations; the generation output reads them without, through the same
    // helpers, so every skipped binding, request or attribute here is exactly one the generated code leaves out.
    // ============================================================================================================

    private static void ReportDiagnostics(
        SourceProductionContext context,
        ImmutableArray<CandidateModel> candidates,
        KnownSnapshot known,
        ImmutableArray<BootstrapCallSiteModel> bootstrapCallSites)
    {
        try
        {
            if (!known.CoreReferenced) return;

            if (known.MissingRequiredTypeNames.Count > 0)
            {
                foreach (var typeName in known.MissingRequiredTypeNames)
                    context.ReportDiagnostic(Diagnostic.Create(FrameworkTypeUnresolvedDiagnostic, Location.None, typeName));
                return;
            }

            ReportNotificationIssues(context, candidates);
            ReportFingerprintIssues(context, candidates);
            ReportRequestHandlerIssues(context, candidates);
            ReportCandidateIssues(context, candidates);
            ReportNotificationHandlerNameIssues(context, candidates);
            ReportContextFactoryIssues(context, candidates, known, bootstrapCallSites);
            ReportComposedNameClashes(context, candidates, known, bootstrapCallSites);
            if (known.VisibleForeignBootstraps > 0)
                ReportBootstrapVisibility(context, known, HasModuleContent(candidates),
                    bootstrapCallSites.Where(site => site.IsPlain).Select(site => site.Location).ToImmutableArray());
        }
        catch (Exception ex)
        {
            ReportCrash(context, ex);
        }
    }

    private static Location LocationOf(CandidateModel candidate) => candidate.Location?.ToLocation() ?? Location.None;

    // CQRGEN005 (a stable-named notification whose shape cannot be serialized), CQRGEN011 (a PartitionBy the selector
    // cannot read, so the ordering would silently never apply) and CQRGEN002 (two notifications, one stable name).
    private static void ReportNotificationIssues(SourceProductionContext context, ImmutableArray<CandidateModel> candidates)
    {
        var notifications = candidates.Where(c => c.Notification is { StableName: not null }).Select(c => c.Notification!).ToArray();

        foreach (var notification in notifications.Where(n => n.OutboxError is not null))
            context.ReportDiagnostic(Diagnostic.Create(
                OutboxNotificationNotSerializableDiagnostic, notification.Location?.ToLocation() ?? Location.None, notification.TypeName, notification.OutboxError));

        foreach (var notification in notifications.Where(n => n.PartitionBy is not null && !n.PartitionByResolved))
            context.ReportDiagnostic(Diagnostic.Create(
                PartitionByPropertyNotFoundDiagnostic, notification.Location?.ToLocation() ?? Location.None, notification.TypeName, notification.PartitionBy));

        foreach (var group in notifications
                     .Where(n => n.OutboxRoot is not null)
                     .GroupBy(n => n.StableName, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
            context.ReportDiagnostic(Diagnostic.Create(
                DuplicateNotificationNameDiagnostic, Location.None, group.Key!, string.Join(", ", group.Select(n => n.TypeName))));
    }

    // CQRGEN014: an idempotent request whose payload the generator cannot render, reported where it was read (the
    // request, or the handler of a request declared elsewhere), once per request.
    private static void ReportFingerprintIssues(SourceProductionContext context, ImmutableArray<CandidateModel> candidates)
    {
        foreach (var (candidate, model) in FingerprintsWithCandidates(candidates).Where(f => f.Model.Error is not null))
            context.ReportDiagnostic(Diagnostic.Create(RequestNotFingerprintableDiagnostic, LocationOf(candidate), model.TypeName, model.Error));
    }

    // CQRGEN003 (a request declared here that nothing here handles) and CQRGEN004 (a request claimed by several
    // handlers: the generated code routes it to the one SelectDeterministicBinding picks, reported there).
    private static void ReportRequestHandlerIssues(SourceProductionContext context, ImmutableArray<CandidateModel> candidates)
    {
        var bindingsByRequest = HandlerBindingsByRequest(candidates);

        foreach (var candidate in candidates.Where(c => c.Request is not null))
            if (!bindingsByRequest.ContainsKey(candidate.Request!.RequestTypeName))
                context.ReportDiagnostic(Diagnostic.Create(MissingRequestHandlerDiagnostic, LocationOf(candidate), candidate.Request.RequestTypeName));

        var locationByName = candidates
            .GroupBy(c => c.TypeName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Location, StringComparer.Ordinal);

        foreach (var bindings in bindingsByRequest.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).Where(b => b.Count > 1))
        {
            var selected = SelectDeterministicBinding(bindings);
            var descriptions = string.Join(", ", bindings
                .OrderBy(b => b.ImplTypeName, StringComparer.Ordinal)
                .ThenBy(b => b.InterfaceNameOrdinal, StringComparer.Ordinal)
                .Select(b => $"{b.ImplTypeName} ({b.InterfaceNameOrdinal})"));
            var location = (locationByName.TryGetValue(selected.ImplTypeName, out var loc) ? loc?.ToLocation() : null) ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(MultipleRequestHandlersDiagnostic, location, selected.Request.RequestTypeName, descriptions));
        }
    }

    // What each type's own model says generated code skips: CQRGEN006 (the type), CQRGEN009 (an open-generic handler),
    // CQRGEN010 (a binding over a type it cannot name), CQRGEN016 (a request attribute it cannot rebuild), CQRGEN017 (a
    // handler over a response its request is not dispatched with).
    private static void ReportCandidateIssues(SourceProductionContext context, ImmutableArray<CandidateModel> candidates)
    {
        foreach (var candidate in candidates)
        {
            var location = LocationOf(candidate);

            if (candidate is { IsConcrete: true, IsAccessible: false, ImplementsAnyKnownHandlerInterface: true })
                context.ReportDiagnostic(Diagnostic.Create(HandlerNotAccessibleDiagnostic, location, candidate.TypeName));

            if (candidate.IsOpenGenericHandler)
                context.ReportDiagnostic(Diagnostic.Create(OpenGenericHandlerDiagnostic, location, candidate.TypeName));

            foreach (var typeName in candidate.InaccessibleBoundTypeNames)
                context.ReportDiagnostic(Diagnostic.Create(InaccessibleBoundTypeDiagnostic, location, candidate.TypeName, typeName));

            foreach (var binding in candidate.Handlers)
            foreach (var skipped in binding.RequestMetadata.SkippedAttributes)
                context.ReportDiagnostic(Diagnostic.Create(RequestAttributeNotReproducibleDiagnostic, location, binding.Request.RequestTypeName, skipped));

            foreach (var mismatch in candidate.ResultMismatches)
                context.ReportDiagnostic(Diagnostic.Create(
                    ResponseTypeMismatchDiagnostic, location, candidate.TypeName, mismatch.InterfaceName, mismatch.DeclaredTypeName, mismatch.RequestTypeName, mismatch.ExpectedTypeName));
        }
    }

    // CQRGEN012 / CQRGEN013: an outbox message is addressed to a handler by its stable name, so within one assembly two
    // handlers may not share a name, and a [NotificationHandlerName] must actually carry one.
    private static void ReportNotificationHandlerNameIssues(SourceProductionContext context, ImmutableArray<CandidateModel> candidates)
    {
        var handlers = candidates.Where(c => c.HandledNotifications.Count > 0 && c.NotificationHandlerName is not null).ToArray();

        foreach (var candidate in handlers.Where(c => c.NotificationHandlerNameIsBlank))
            context.ReportDiagnostic(Diagnostic.Create(BlankNotificationHandlerNameDiagnostic, LocationOf(candidate), candidate.TypeName));

        foreach (var group in handlers
                     .GroupBy(c => c.NotificationHandlerName!, StringComparer.Ordinal)
                     .Where(g => g.Select(c => c.TypeName).Distinct(StringComparer.Ordinal).Count() > 1)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var types = string.Join(", ", group.Select(c => c.TypeName).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal));
            foreach (var candidate in group)
                context.ReportDiagnostic(Diagnostic.Create(DuplicateNotificationHandlerNameDiagnostic, LocationOf(candidate), group.Key, types));
        }
    }

    // CQRGEN018 / CQRGEN019: a context type has one factory. Two declared in this assembly are a mistake, reported at each.
    // Two declared in referenced assemblies, with none here, are resolved by the order their modules are registered in,
    // which nobody chose: reported where this assembly composes them (its AddCqrsGenerated calls), and not at all in
    // an assembly that never does. This assembly's own factory replaces theirs.
    private static void ReportContextFactoryIssues(
        SourceProductionContext context,
        ImmutableArray<CandidateModel> candidates,
        KnownSnapshot known,
        ImmutableArray<BootstrapCallSiteModel> callSites)
    {
        var declaredHere = candidates
            .Where(c => c.RegistersContextFactories)
            .SelectMany(c => c.ContextFactories.Select(contextType => (ContextType: contextType, Candidate: c)))
            .GroupBy(f => f.ContextType, StringComparer.Ordinal)
            .ToArray();

        foreach (var group in declaredHere.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var factories = group.Select(f => f.Candidate).GroupBy(c => c.TypeName, StringComparer.Ordinal).Select(g => g.First()).ToArray();
            if (factories.Length < 2) continue;

            var names = string.Join(", ", factories.Select(c => c.TypeName).OrderBy(n => n, StringComparer.Ordinal));
            foreach (var candidate in factories)
                context.ReportDiagnostic(Diagnostic.Create(DuplicateContextFactoryDiagnostic, LocationOf(candidate), group.Key, names));
        }

        if (callSites.Length == 0 || known.AmbiguousReferencedContextFactories.Count == 0) return;

        var ownContextTypes = new HashSet<string>(declaredHere.Select(g => g.Key), StringComparer.Ordinal);
        foreach (var group in known.AmbiguousReferencedContextFactories.GroupBy(f => f.ContextTypeName, StringComparer.Ordinal))
        {
            if (ownContextTypes.Contains(group.Key)) continue;

            var ordered = group.OrderBy(f => f.RegistrarName, StringComparer.Ordinal).ToArray();
            var factories = string.Join(", ", ordered.Select(f => $"'{f.FactoryTypeName}' in '{f.AssemblyName}'"));
            foreach (var site in callSites)
                context.ReportDiagnostic(Diagnostic.Create(
                    AmbiguousContextFactoryDiagnostic, site.Location.ToLocation(), ordered[0].ContextDisplayName, factories, ordered[ordered.Length - 1].FactoryTypeName));
        }
    }

    // CQRGEN020 / CQRGEN021: the stable names of the modules this assembly composes, its own and its references', must
    // not clash across them (a clash within one assembly is CQRGEN002 / CQRGEN012 there). The runtime resolves a
    // notification name to the module registered last, this assembly's own before any other, and reports both clashes
    // as errors once the outbox is on; whether it will be on is not known here, so they are warnings, reported where
    // this assembly composes the modules (its AddCqrsGenerated calls) and not at all in an assembly that never does.
    private static void ReportComposedNameClashes(
        SourceProductionContext context,
        ImmutableArray<CandidateModel> candidates,
        KnownSnapshot known,
        ImmutableArray<BootstrapCallSiteModel> callSites)
    {
        if (callSites.Length == 0) return;

        // This assembly's module registers last, which an ordinal sort puts after every registrar name.
        const string ownRegistrar = "\uffff";
        var ownAssembly = "this assembly";

        var notifications = candidates
            .Where(c => c.Notification is { StableName: not null, OutboxRoot: not null })
            .Select(c => new ReferencedNameModel(c.Notification!.StableName!, c.Notification.TypeName, ownAssembly, ownRegistrar))
            .Concat(known.ReferencedNotificationNames);
        foreach (var clash in Clashes(notifications))
        {
            var keeper = clash.OrderBy(n => n.RegistrarName, StringComparer.Ordinal).Last();
            foreach (var site in callSites)
                context.ReportDiagnostic(Diagnostic.Create(
                    ComposedNotificationNameClashDiagnostic, site.Location.ToLocation(), clash.Key, Describe(clash), Display(keeper.TypeName)));
        }

        var handlers = candidates
            .Where(c => c.HandledNotifications.Count > 0 && c.NotificationHandlerName is not null)
            .Select(c => new ReferencedNameModel(c.NotificationHandlerName!, c.TypeName, ownAssembly, ownRegistrar))
            .Concat(known.ReferencedHandlerNames);
        foreach (var clash in Clashes(handlers))
            foreach (var site in callSites)
                context.ReportDiagnostic(Diagnostic.Create(ComposedHandlerNameClashDiagnostic, site.Location.ToLocation(), clash.Key, Describe(clash)));

        // A name given to more than one type (a type is its name and its assembly: two assemblies may declare the same
        // namespace-qualified name) by more than one assembly.
        static IEnumerable<IGrouping<string, ReferencedNameModel>> Clashes(IEnumerable<ReferencedNameModel> names)
            => names
                .GroupBy(n => n.Name, StringComparer.Ordinal)
                .Where(g => g.Select(n => (n.TypeName, n.AssemblyName)).Distinct().Count() > 1 &&
                            g.Select(n => n.AssemblyName).Distinct(StringComparer.Ordinal).Count() > 1)
                .OrderBy(g => g.Key, StringComparer.Ordinal);

        static string Describe(IEnumerable<ReferencedNameModel> clash)
            => string.Join(", ", clash
                .Select(n => $"'{Display(n.TypeName)}' in {(n.RegistrarName == ownRegistrar ? n.AssemblyName : "'" + n.AssemblyName + "'")}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(d => d, StringComparer.Ordinal));

        static string Display(string typeName) => typeName.StartsWith("global::", StringComparison.Ordinal) ? typeName.Substring("global::".Length) : typeName;
    }

    // CQRGEN015: a referenced assembly's internal bootstrap is visible here. A warning at every plain call site when
    // such a call is wrong (ambiguous, or bound to the other assembly's copy while this assembly has a module of its
    // own); otherwise a note, so the assembly's own entry point is still discoverable.
    private static void ReportBootstrapVisibility(
        SourceProductionContext context,
        KnownSnapshot known,
        bool hasModule,
        ImmutableArray<LocationInfo> callSites)
    {
        var entryPoint = $"{known.ModuleNamespace}.{CqrsKnownSymbols.BootstrapTypeName}.AddCqrsGenerated(services)";
        var plainCallIsWrong = hasModule || known.VisibleForeignBootstraps > 1 || !known.ForeignBootstrapCoversGraph;

        if (plainCallIsWrong && callSites.Length > 0)
        {
            foreach (var site in callSites)
                context.ReportDiagnostic(Diagnostic.Create(BootstrapVisibilityDiagnostic, site.ToLocation(), entryPoint, known.ModuleNamespace));
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            BootstrapVisibilityDiagnostic, Location.None, DiagnosticSeverity.Info, null, null, entryPoint, known.ModuleNamespace));
    }
}
