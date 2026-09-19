using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using CQRSharp.Generators;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly SymbolDisplayFormat FqNullable =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // ============================================================================================================
    // Compilation-level snapshot (resolved once per compilation, equatable).
    // ============================================================================================================

    private static KnownSnapshot CreateKnownSnapshot(Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);
        var hasBuilder = compilation.GetTypeByMetadataName("CQRSharp.Pipelines.Extensions.ICqrsBuilder") is not null;
        var commandResult = known.CommandResult?.ToDisplayString(Fq) ?? string.Empty;
        var contextBase = known.RequestContextBase?.ToDisplayString(Fq) ?? string.Empty;
        var preHandler = known.IPreHandlerAttribute?.ToDisplayString(Fq) ?? string.Empty;
        var postHandler = known.IPostHandlerAttribute?.ToDisplayString(Fq) ?? string.Empty;
        var pipelineExemption = known.PipelineExemptionAttribute?.ToDisplayString(Fq) ?? string.Empty;
        var missing = known.GetMissingRequiredRoleNames().ToArray();
        return new KnownSnapshot(
            known.AbstractionsPresent,
            known.CoreReferenced,
            new EquatableArray<string>(missing),
            hasBuilder,
            commandResult,
            contextBase,
            preHandler,
            postHandler,
            pipelineExemption,
            ModuleNamespaceFor(compilation.AssemblyName),
            new EquatableArray<string>(GetReferencedModuleRegistrars(compilation)));
    }

    // The per-assembly namespace the generated module (dispatchers, registries, registrar) lives in. Keying it to the
    // assembly name means two assemblies in one reference graph never emit colliding generated types — the root cause
    // of the multi-assembly CS0121. Sanitized to a single valid identifier; assembly names are unique within a graph.
    private static string ModuleNamespaceFor(string? assemblyName)
    {
        var name = string.IsNullOrEmpty(assemblyName) ? "Anonymous" : assemblyName!;
        var sb = new StringBuilder("CQRSharp.Generated.");
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
            if (i == 0 && c >= '0' && c <= '9') sb.Append('_');
            sb.Append(ok ? c : '_');
        }

        return sb.ToString();
    }

    // The generated module registrars of every referenced assembly that ran the generator, read from the
    // [assembly: CqrsGeneratedModule(typeof(...))] markers. A composition root emits a Register call for each, so a
    // single AddCqrsGenerated wires its whole reference graph at compile time (no runtime assembly scanning).
    private static string[] GetReferencedModuleRegistrars(Compilation compilation)
    {
        var moduleAttribute = compilation.GetTypeByMetadataName(
            "CQRSharp.Abstractions.Attributes.SourceGeneration.CqrsGeneratedModuleAttribute");
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
        // "multiple handlers" (CQRGEN004) and emit duplicate dispatcher arms / notification names.
        var declarations = type.DeclaringSyntaxReferences;
        if (declarations.Length > 1 &&
            (declarations[0].SyntaxTree != ctx.Node.SyntaxTree || declarations[0].Span != ctx.Node.Span))
            return null;

        var compilation = ctx.SemanticModel.Compilation;
        var known = CqrsKnownSymbols.For(compilation);
        if (!known.AbstractionsPresent) return null;

        var isConcrete = type is { IsAbstract: false, IsGenericType: false };
        var isAccessible = IsAccessibleFromGeneratedCode(type);

        var allKnownHandlers = GetAllKnownHandlerSymbols(compilation)
            .Select(s => s.OriginalDefinition)
            .ToArray();

        var implementsAnyHandler = isConcrete && GetInterfacesAndBaseInterfaces(type)
            .Any(i => i.IsGenericType && allKnownHandlers.Contains(i.OriginalDefinition, SymbolEqualityComparer.Default));

        var handlers = isConcrete && isAccessible
            ? BuildHandlerImpls(type, compilation)
            : Array.Empty<HandlerImplModel>();

        // No sort/distinct: the registration generator emits forwarders in this exact interface-iteration order.
        var forwarders = isConcrete && isAccessible
            ? GetInterfacesAndBaseInterfaces(type)
                .Where(i => i.IsGenericType && allKnownHandlers.Contains(i.OriginalDefinition, SymbolEqualityComparer.Default))
                .Select(i => i.ToDisplayString(FqNullable))
                .ToArray()
            : Array.Empty<string>();

        var request = isConcrete && isAccessible ? BuildRequest(type, compilation) : null;
        var notification = isConcrete && isAccessible ? BuildNotification(type, compilation) : null;
        var handledNotifications = isConcrete && isAccessible
            ? BuildHandledNotifications(type, compilation)
            : Array.Empty<string>();
        var contextFactories = !type.IsAbstract && isAccessible
            ? BuildContextFactories(type, compilation)
            : Array.Empty<string>();
        var exceptionHooks = isConcrete && isAccessible
            ? BuildExceptionHooks(type, compilation)
            : Array.Empty<ExceptionHookModel>();
        var aotBehavior = BuildAotOpenGenericBehavior(type, compilation, isAccessible);

        // CQRGEN010: dispatch-handler type arguments that are too inaccessible for generated registration. The handler
        // itself is accessible (so CQRGEN006 does not fire), but the binding is silently dropped in BuildHandlerImpls —
        // surfacing only as a runtime "no handler". Carry the offending type names so they can be flagged at the handler.
        var inaccessibleBoundTypes = isConcrete && isAccessible
            ? CollectInaccessibleBoundTypes(type, compilation)
            : Array.Empty<string>();

        // An open-generic dispatch handler (e.g. `class H<T> : ICommandHandler<C<T>>`) is silently unregistered — the
        // generator wires only closed, non-generic handlers — so it would fail only as a runtime "no handler". Carry it
        // through so CQRGEN009 can flag it at the declaration. (Open-generic pipeline behaviors ARE supported and handled
        // separately via AotOpenGenericBehaviorName, so they are excluded here.)
        var isOpenGenericHandler = type is { IsAbstract: false, IsGenericType: true } &&
                                   ImplementsDispatchHandlerInterface(type, compilation);

        // Drop types that are not CQRSharp-relevant in any way, to keep the collected set small.
        if (!implementsAnyHandler &&
            !isOpenGenericHandler &&
            handlers.Length == 0 &&
            forwarders.Length == 0 &&
            request is null &&
            notification is null &&
            handledNotifications.Length == 0 &&
            contextFactories.Length == 0 &&
            exceptionHooks.Length == 0 &&
            aotBehavior is null)
            return null;

        return new CandidateModel(
            type.ToDisplayString(Fq),
            isAccessible,
            isConcrete,
            LocationInfo.CreateFrom(type),
            implementsAnyHandler,
            new EquatableArray<HandlerImplModel>(handlers),
            new EquatableArray<string>(forwarders),
            request,
            notification,
            new EquatableArray<string>(handledNotifications),
            new EquatableArray<string>(contextFactories),
            new EquatableArray<ExceptionHookModel>(exceptionHooks),
            aotBehavior,
            isOpenGenericHandler,
            new EquatableArray<string>(inaccessibleBoundTypes));
    }
}
