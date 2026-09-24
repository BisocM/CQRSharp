using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CQRSharp.Generators;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly SymbolDisplayFormat FqNullable =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // ============================================================================================================
    // Compilation-level snapshot (recomputed per compilation; equatable, so the steps after it re-run only on change).
    // ============================================================================================================

    private static KnownSnapshot CreateKnownSnapshot(Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);
        var moduleNamespace = CqrsKnownSymbols.ModuleNamespaceFor(compilation.AssemblyName);
        var none = new EquatableArray<string>(Array.Empty<string>());
        if (!known.CoreReferenced)
            return new KnownSnapshot(false, none, false, moduleNamespace, none, 0, true,
                new EquatableArray<ClosedBehaviorModel>(Array.Empty<ClosedBehaviorModel>()),
                new EquatableArray<ClosedBehaviorGapModel>(Array.Empty<ClosedBehaviorGapModel>()),
                new EquatableArray<ReferencedContextFactoryModel>(Array.Empty<ReferencedContextFactoryModel>()));

        var (referencedClosed, referencedGaps) = GetReferencedClosedBehaviors(compilation, known);
        return new KnownSnapshot(
            true,
            new EquatableArray<string>(known.GetMissingRequiredTypeNames().ToArray()),
            known.ICqrsBuilder is not null,
            moduleNamespace,
            new EquatableArray<string>(GetReferencedModuleRegistrars(compilation, known)),
            CountVisibleForeignBootstraps(compilation),
            ForeignBootstrapCoversGraph(compilation, known),
            new EquatableArray<ClosedBehaviorModel>(referencedClosed),
            new EquatableArray<ClosedBehaviorGapModel>(referencedGaps),
            new EquatableArray<ReferencedContextFactoryModel>(GetAmbiguousReferencedContextFactories(compilation, known)));
    }

    // The context types more than one referenced module registers a factory for ([CqrsRegisteredContextFactory] markers
    // name the context types an assembly has a factory for), with those factories. A marker also stands for a generic
    // factory, which generated code never registers, so each assembly is checked for one it does register: a concrete
    // class generated code there could name. Only the few context types with several markers are checked.
    private static ReferencedContextFactoryModel[] GetAmbiguousReferencedContextFactories(Compilation compilation, CqrsKnownSymbols known)
    {
        var marker = known.CqrsRegisteredContextFactoryAttribute;
        var moduleMarker = known.CqrsGeneratedModuleAttribute;
        var factoryInterface = known.IRequestContextFactory;
        if (marker is null || moduleMarker is null || factoryInterface is null) return Array.Empty<ReferencedContextFactoryModel>();

        var declaring = new Dictionary<INamedTypeSymbol, List<(IAssemblySymbol Assembly, string Registrar)>>(SymbolEqualityComparer.Default);
        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            var attributes = reference.GetAttributes();
            var registrar = attributes
                .Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, moduleMarker))
                .Select(a => a.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol)
                .FirstOrDefault(r => r is not null);
            if (registrar is null) continue;

            foreach (var attribute in attributes)
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker)) continue;
                if (attribute.ConstructorArguments.FirstOrDefault().Value is not INamedTypeSymbol context) continue;

                if (!declaring.TryGetValue(context, out var assemblies)) declaring[context] = assemblies = new List<(IAssemblySymbol, string)>();
                assemblies.Add((reference, registrar.ToDisplayString(Fq)));
            }
        }

        var ambiguous = new List<ReferencedContextFactoryModel>();
        foreach (var entry in declaring.Where(e => e.Value.Count > 1))
        {
            var service = factoryInterface.Construct(entry.Key);
            var registered = entry.Value
                .Select(d => (d.Assembly, d.Registrar, Factory: RegisteredFactoryOf(d.Assembly, service)))
                .Where(d => d.Factory is not null)
                .ToArray();
            if (registered.Length < 2) continue;

            var contextTypeName = entry.Key.ToDisplayString(Fq);
            var contextDisplayName = entry.Key.ToDisplayString();
            ambiguous.AddRange(registered.Select(d =>
                new ReferencedContextFactoryModel(contextTypeName, contextDisplayName, d.Factory!.ToDisplayString(), d.Assembly.Name, d.Registrar)));
        }

        return ambiguous
            .OrderBy(f => f.ContextTypeName, StringComparer.Ordinal)
            .ThenBy(f => f.RegistrarName, StringComparer.Ordinal)
            .ToArray();
    }

    // The factory of the service a module generated in that assembly registers: a non-generic, non-abstract class that
    // implements it and that generated code there can name (not nested in a type it cannot see into).
    private static INamedTypeSymbol? RegisteredFactoryOf(IAssemblySymbol assembly, INamedTypeSymbol service)
    {
        return Find(assembly.GlobalNamespace);

        INamedTypeSymbol? Find(INamespaceSymbol ns)
        {
            foreach (var child in ns.GetNamespaceMembers())
                if (Find(child) is { } found)
                    return found;
            foreach (var type in ns.GetTypeMembers())
                if (FindType(type) is { } found)
                    return found;
            return null;
        }

        INamedTypeSymbol? FindType(INamedTypeSymbol type)
        {
            if (type.IsGenericType || type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                return null;
            if (type is { TypeKind: TypeKind.Class, IsAbstract: false } && type.AllInterfaces.Contains(service, SymbolEqualityComparer.Default))
                return type;

            foreach (var nested in type.GetTypeMembers())
                if (FindType(nested) is { } found)
                    return found;
            return null;
        }
    }

    // Whether the one visible foreign bootstrap registers every module this assembly references: it registers its own
    // assembly's module and those of its references, so a plain AddCqrsGenerated() bound to it skips any other.
    private static bool ForeignBootstrapCoversGraph(Compilation compilation, CqrsKnownSymbols known)
    {
        var moduleAttribute = known.CqrsGeneratedModuleAttribute;
        if (moduleAttribute is null) return true;

        IAssemblySymbol? foreign = null;
        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
            if (reference.GivesAccessTo(compilation.Assembly) &&
                reference.GetTypeByMetadataName("CQRSharp." + CqrsKnownSymbols.BootstrapTypeName) is not null)
            {
                if (foreign is not null) return false;
                foreign = reference;
            }

        if (foreign is null) return true;

        var covered = new HashSet<string>(StringComparer.Ordinal) { foreign.Name };
        foreach (var module in foreign.Modules)
        foreach (var referenced in module.ReferencedAssemblySymbols)
            covered.Add(referenced.Name);

        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
            if (!covered.Contains(reference.Name) &&
                reference.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, moduleAttribute)))
                return false;

        return true;
    }

    // The value-type-result requests the referenced assemblies handle (their [CqrsHandledRequest] markers) and the
    // value-type notifications they declare or handle, with every open-generic behavior this assembly can see closed over
    // them: a library's module closes only the behaviors the library sees, and under Native AOT the host's own behaviors
    // must wrap those requests and notifications too. One this assembly cannot name gets its behaviors as gaps, named the
    // way the runtime sees them.
    private static (ClosedBehaviorModel[] Closed, ClosedBehaviorGapModel[] Gaps) GetReferencedClosedBehaviors(Compilation compilation, CqrsKnownSymbols known)
    {
        var closed = new List<ClosedBehaviorModel>();
        var gaps = new List<ClosedBehaviorGapModel>();

        if (known.CqrsHandledRequestAttribute is { } marker && (known.IPipelineBehavior is not null || known.IStreamPipelineBehavior is not null))
            foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
            foreach (var attribute in reference.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker)) continue;
                if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol request) continue;
                if (known.ShapeOf(request) is not { ResultOrItem: { IsValueType: true } result } shape) continue;

                var behaviors = BuildClosedBehaviors(request, new[] { request, result }, KindOf(shape), compilation, known);
                closed.AddRange(behaviors.Closed);
                gaps.AddRange(behaviors.Gaps);
            }

        // Walking the references for value-type notifications is only worth it when a notification behavior could apply.
        if (OpenBehaviorsOf(compilation).Any(b => b.Kind == BehaviorKind.Notification))
        {
            var behaviors = BuildNotificationClosedBehaviors(ReferencedValueTypeNotificationsOf(compilation), compilation, known);
            closed.AddRange(behaviors.Closed);
            gaps.AddRange(behaviors.Gaps);
        }

        return (
            closed.Distinct().OrderBy(c => c.ServiceTypeName, StringComparer.Ordinal).ThenBy(c => c.OpenTypeName, StringComparer.Ordinal).ToArray(),
            gaps.Distinct().OrderBy(g => g.TargetRuntimeName, StringComparer.Ordinal).ThenBy(g => g.BehaviorRuntimeName, StringComparer.Ordinal).ToArray());
    }

    // The bootstrap is internal so that two assemblies' copies never collide - unless one of them exposes its
    // internals to the other (InternalsVisibleTo: an app and its test project), in which case the other assembly's
    // AddCqrsGenerated is visible here too. The bootstrap emitter and CQRGEN015 act on how many there are.
    private static int CountVisibleForeignBootstraps(Compilation compilation)
    {
        var count = 0;
        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (!reference.GivesAccessTo(compilation.Assembly)) continue;
            if (reference.GetTypeByMetadataName("CQRSharp." + CqrsKnownSymbols.BootstrapTypeName) is not null) count++;
        }

        return count;
    }

    // The generated module registrars of every referenced assembly that ran the generator, read from the
    // [assembly: CqrsGeneratedModule(typeof(...))] markers. A composition root emits a Register call for each, so a
    // single AddCqrsGenerated wires its whole reference graph at compile time (no runtime assembly scanning).
    private static string[] GetReferencedModuleRegistrars(Compilation compilation, CqrsKnownSymbols known)
    {
        var moduleAttribute = known.CqrsGeneratedModuleAttribute;
        if (moduleAttribute is null) return Array.Empty<string>();

        var registrars = new List<string>();
        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
        foreach (var attribute in reference.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, moduleAttribute)) continue;
            if (attribute.ConstructorArguments.Length == 0) continue;
            if (attribute.ConstructorArguments[0].Value is INamedTypeSymbol registrar)
                registrars.Add(registrar.ToDisplayString(Fq));
        }

        registrars.Sort(StringComparer.Ordinal);
        return registrars.ToArray();
    }

    // ============================================================================================================
    // Per-candidate transform: project a class/record declaration into an equatable CandidateModel (or null when it
    // is not CQRSharp-relevant). Every symbol read happens here; nothing symbol-bearing escapes into the pipeline.
    // ============================================================================================================

    private static CandidateModel? TransformCandidate(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol type) return null;

        // A partial type reaches this transform once per declaration, each resolving to the same merged symbol. Project
        // it from its first declaration only: duplicate candidates would otherwise report a single handler as
        // "multiple handlers" (CQRGEN004) and emit duplicate routes and notification names.
        var declarations = type.DeclaringSyntaxReferences;
        if (declarations.Length > 1 &&
            (declarations[0].SyntaxTree != ctx.Node.SyntaxTree || declarations[0].Span != ctx.Node.Span))
            return null;

        var compilation = ctx.SemanticModel.Compilation;
        var known = CqrsKnownSymbols.For(compilation);
        if (!known.CoreReferenced) return null;

        var isConcrete = type is { IsAbstract: false, IsGenericType: false };
        var isAccessible = GeneratedCodeAccessibility.IsAccessible(type, compilation);
        var registrable = isConcrete && isAccessible;

        var implementsAnyHandler = isConcrete && type.AllInterfaces.Any(known.IsRegisteredRole);

        // Every binding generated code has to skip, and why: a type it cannot name (CQRGEN010), a response the request
        // is not dispatched with (CQRGEN017). Collected alongside the bindings so the diagnostics and the drops agree.
        var inaccessible = new List<string>();
        var mismatches = new List<ResultMismatchModel>();

        var (forwarders, discoveredServices) = registrable
            ? BuildForwarders(type, compilation, known, inaccessible)
            : (Array.Empty<string>(), Array.Empty<string>());
        var handlers = registrable ? BuildHandlerImpls(type, compilation, known, mismatches) : Array.Empty<HandlerImplModel>();
        var request = registrable ? BuildRequest(type, compilation, known, inaccessible) : null;
        var fingerprint = request is not null ? BuildFingerprint(type, compilation, known) : null;
        var handledRequestFingerprints = registrable
            ? BuildHandledRequestFingerprints(type, compilation, known)
            : Array.Empty<FingerprintModel>();
        var notification = registrable ? BuildNotification(type, compilation, known) : null;
        var handledNotificationTypes = registrable
            ? BuildHandledNotifications(type, compilation, known)
            : Array.Empty<(ITypeSymbol Type, string Name, bool IsConcrete)>();
        var handledNotifications = handledNotificationTypes.Select(n => n.Name).ToArray();
        var handledConcreteNotifications = handledNotificationTypes.Where(n => n.IsConcrete).Select(n => n.Name).ToArray();
        var (handlerName, handlerNameIsBlank) = handledNotifications.Length > 0
            ? BuildNotificationHandlerName(type, known)
            : (null, false);
        var contextFactories = !type.IsAbstract && isAccessible
            ? BuildContextFactories(type, compilation, known)
            : Array.Empty<string>();
        var exceptionHooks = registrable
            ? BuildExceptionHooks(type, compilation, known, mismatches)
            : Array.Empty<ExceptionHookModel>();

        // The value-type notifications this type brings into the module: itself when it is one (declared here, even where
        // generated code cannot name it, so its behaviors are recorded as gaps rather than silently skipped), and the
        // concrete ones it handles. Their notification behaviors are closed at compile time: the container cannot close
        // an open-generic one over a value type without dynamic code.
        var valueTypeNotifications = handledNotificationTypes
            .Where(n => n.IsConcrete && n.Type is INamedTypeSymbol { IsValueType: true })
            .Select(n => (INamedTypeSymbol)n.Type);
        if (isConcrete && type.IsValueType && known.INotification is { } notificationInterface &&
            type.AllInterfaces.Contains(notificationInterface, SymbolEqualityComparer.Default))
            valueTypeNotifications = valueTypeNotifications.Prepend(type);
        var (notificationClosed, notificationGaps) = BuildNotificationClosedBehaviors(valueTypeNotifications, compilation, known);

        // An open-generic dispatch handler (e.g. `class H<T> : ICommandHandler<C<T>>`) is silently unregistered — the
        // generator wires only closed, non-generic handlers — so it would fail only as a runtime "no handler". Carry it
        // through so CQRGEN009 can flag it at the declaration. Open-generic pipeline behaviors are supported: the container
        // closes them, and the closed-behavior factories do so for value-type results.
        var isOpenGenericHandler = type is { IsAbstract: false, IsGenericType: true } &&
                                   type.AllInterfaces.Any(known.IsDispatchHandler);

        // Drop types that are not CQRSharp-relevant in any way, to keep the collected set small.
        if (!implementsAnyHandler &&
            !isOpenGenericHandler &&
            handlers.Length == 0 &&
            forwarders.Length == 0 &&
            discoveredServices.Length == 0 &&
            request is null &&
            notification is null &&
            handledNotifications.Length == 0 &&
            contextFactories.Length == 0 &&
            exceptionHooks.Length == 0 &&
            notificationClosed.Length == 0 &&
            notificationGaps.Length == 0 &&
            inaccessible.Count == 0 &&
            mismatches.Count == 0)
            return null;

        return new CandidateModel(
            type.ToDisplayString(Fq),
            isAccessible,
            isConcrete,
            LocationInfo.CreateFrom(type),
            implementsAnyHandler,
            new EquatableArray<HandlerImplModel>(handlers),
            new EquatableArray<string>(forwarders),
            new EquatableArray<string>(discoveredServices),
            request,
            fingerprint,
            new EquatableArray<FingerprintModel>(handledRequestFingerprints),
            notification,
            new EquatableArray<string>(handledNotifications),
            new EquatableArray<string>(handledConcreteNotifications),
            handlerName,
            handlerNameIsBlank,
            new EquatableArray<string>(contextFactories),
            // The container creates a factory through its constructor, which only a class has to offer.
            contextFactories.Length > 0 && type is { IsGenericType: false, TypeKind: TypeKind.Class },
            new EquatableArray<ExceptionHookModel>(exceptionHooks),
            new EquatableArray<ClosedBehaviorModel>(notificationClosed),
            new EquatableArray<ClosedBehaviorGapModel>(notificationGaps),
            isOpenGenericHandler,
            new EquatableArray<string>(inaccessible.Distinct(StringComparer.Ordinal).ToArray()),
            new EquatableArray<ResultMismatchModel>(mismatches.Distinct().ToArray()),
            new EquatableArray<string>(CandidateSuppressedWarnings(type, known, notification is not null || fingerprint is not null || handledRequestFingerprints.Length > 0)));
    }

    // What generated code naming this type, and the types it binds, has to suppress: the type itself, the type arguments
    // of every interface it implements and of every base class (its request, response, notification, context and
    // exception types), and, for what the outbox serializer or fingerprinter read member by member, those members.
    private static string[] CandidateSuppressedWarnings(INamedTypeSymbol type, CqrsKnownSymbols known, bool readsMembers)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        GeneratedCodeSuppressions.CollectType(type, ids);
        foreach (var iface in type.AllInterfaces)
            GeneratedCodeSuppressions.CollectType(iface, ids);
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            GeneratedCodeSuppressions.CollectType(baseType, ids);

        if (readsMembers)
        {
            GeneratedCodeSuppressions.CollectMembers(type, ids);
            foreach (var iface in type.AllInterfaces)
                if (known.IsDispatchHandler(iface))
                    GeneratedCodeSuppressions.CollectMembers(iface.TypeArguments[0], ids);
        }

        return ids.ToArray();
    }
}
