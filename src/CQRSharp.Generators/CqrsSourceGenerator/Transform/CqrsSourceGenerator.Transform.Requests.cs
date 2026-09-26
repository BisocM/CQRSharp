using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CQRSharp.Generators;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    // The request registry reads pre/post/exemption attributes and the context type from the request type itself,
    // via the handler binding, so it is captured here (and works for request types in referenced assemblies too).
    private static RequestMetadataModel BuildRequestMetadata(ITypeSymbol requestType, Compilation compilation, CqrsKnownSymbols known)
    {
        var defaultContext = known.RequestContextBase;
        var contextSymbol = known.DeclaredContextOf(requestType) ?? defaultContext;
        if (contextSymbol is null || !GeneratedCodeAccessibility.IsAccessible(contextSymbol, compilation))
            contextSymbol = defaultContext;
        var contextName = contextSymbol?.ToDisplayString(Fq) ?? "global::System.Object";

        var skipped = new List<string>();
        return new RequestMetadataModel(
            contextName,
            new EquatableArray<AttributeModel>(BuildAttributeModels(requestType, known.IPreHandlerAttribute, compilation, known, skipped)),
            new EquatableArray<AttributeModel>(BuildAttributeModels(requestType, known.IPostHandlerAttribute, compilation, known, skipped)),
            new EquatableArray<AttributeModel>(BuildAttributeModels(requestType, known.PipelineExemptionAttribute, compilation, known, skipped)),
            new EquatableArray<string>(skipped.ToArray()));
    }

    // A request declared here, dispatched by its shape. One whose result generated code cannot name (an IQuery<Row> over
    // a private Row, legal because a class may implement a less accessible interface) is not routed: it is reported
    // instead (CQRGEN010), as its handler's binding is.
    private static RequestModel? BuildRequest(INamedTypeSymbol type, Compilation compilation, CqrsKnownSymbols known, List<string> inaccessible)
    {
        if (known.ShapeOf(type) is not { } shape) return null;

        if (!GeneratedCodeAccessibility.IsAccessible(shape.Response, compilation))
        {
            inaccessible.Add(InaccessiblePartOf(shape.Response, compilation).ToDisplayString(Fq));
            return null;
        }

        return BuildRequestModel(type, shape, compilation, known);
    }

    /// <summary>
    ///     Every open-generic behavior of <paramref name="kind" /> in view closed over <paramref name="target" /> (a request
    ///     whose result or streamed item is a value type, or a value-type notification): a factory for each one generated
    ///     code can close, a gap for each one it cannot (so the runtime can refuse to skip it). A behavior whose constraints
    ///     exclude the target is neither, and one a request exempts is no gap: it never runs for it anyway.
    /// </summary>
    /// <param name="target">The request or notification.</param>
    /// <param name="arguments">The behavior interface's type arguments: (request, result or item), or (notification).</param>
    private static (ClosedBehaviorModel[] Closed, ClosedBehaviorGapModel[] Gaps) BuildClosedBehaviors(
        INamedTypeSymbol target,
        ITypeSymbol[] arguments,
        BehaviorKind kind,
        Compilation compilation,
        CqrsKnownSymbols known)
    {
        var serviceDefinition = kind switch
        {
            BehaviorKind.Stream => known.IStreamPipelineBehavior,
            BehaviorKind.Notification => known.INotificationPipelineBehavior,
            _ => known.IPipelineBehavior
        };
        if (serviceDefinition is null) return (Array.Empty<ClosedBehaviorModel>(), Array.Empty<ClosedBehaviorGapModel>());

        var service = serviceDefinition.Construct(arguments);
        var targetNameable = GeneratedCodeAccessibility.IsAccessible(service, compilation);
        var serviceTypeName = service.ToDisplayString(Fq);
        var noun = kind == BehaviorKind.Notification ? "notification" : "request";
        var closed = new List<ClosedBehaviorModel>();
        var gaps = new List<ClosedBehaviorGapModel>();
        foreach (var behavior in OpenBehaviorsOf(compilation))
        {
            if (behavior.Kind != kind) continue;
            if (!SatisfiesConstraints(behavior.Type, arguments, compilation)) continue;

            var blocked = behavior.Blocked ?? (targetNameable ? null : $"the {noun} '{target.ToDisplayString()}' is not accessible to '{compilation.AssemblyName}'");
            if (blocked is null)
            {
                closed.Add(new ClosedBehaviorModel(
                    behavior.Type.ConstructUnboundGenericType().ToDisplayString(Fq),
                    serviceTypeName,
                    behavior.Type.Construct(arguments).ToDisplayString(Fq),
                    behavior.SuppressedWarnings,
                    RelaxesNotNull(behavior.Type, arguments)));
                continue;
            }

            // Exemptions are a request's: nothing exempts a notification from its behaviors.
            if (kind != BehaviorKind.Notification && IsExempted(target, behavior.Type, arguments[1], known)) continue;
            if (ClosedWhereDeclared(target, behavior.Type, compilation, known)) continue;

            // A target generated code cannot name is matched by its runtime name, which for a constructed generic carries
            // its arguments' assembly-qualified names (versions included) - nothing generated code can spell.
            if (!targetNameable && target.IsGenericType) continue;

            gaps.Add(new ClosedBehaviorGapModel(
                targetNameable ? serviceTypeName : null,
                RuntimeNameOf(target),
                target.ContainingAssembly.Name,
                serviceDefinition.ConstructUnboundGenericType().ToDisplayString(Fq),
                RuntimeNameOf(behavior.Type),
                behavior.Type.ContainingAssembly.Name,
                blocked));
        }

        return (
            closed.OrderBy(c => c.OpenTypeName, StringComparer.Ordinal).ToArray(),
            gaps.OrderBy(g => g.BehaviorRuntimeName, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    ///     The behaviors of every value-type notification in <paramref name="notifications" />, closed where generated code
    ///     can and recorded as gaps where it cannot (see <see cref="BuildClosedBehaviors" />): Microsoft's container cannot
    ///     close an open-generic notification behavior over a value type without dynamic code.
    /// </summary>
    private static (ClosedBehaviorModel[] Closed, ClosedBehaviorGapModel[] Gaps) BuildNotificationClosedBehaviors(
        IEnumerable<INamedTypeSymbol> notifications,
        Compilation compilation,
        CqrsKnownSymbols known)
    {
        List<ClosedBehaviorModel>? closed = null;
        List<ClosedBehaviorGapModel>? gaps = null;
        foreach (var notification in notifications)
        {
            // A ref struct is never a generic argument, so nothing closes a behavior over it (nor can it be published).
            if (!notification.IsValueType || notification.IsRefLikeType) continue;

            var behaviors = BuildClosedBehaviors(notification, new ITypeSymbol[] { notification }, BehaviorKind.Notification, compilation, known);
            if (behaviors.Closed.Length > 0) (closed ??= new List<ClosedBehaviorModel>()).AddRange(behaviors.Closed);
            if (behaviors.Gaps.Length > 0) (gaps ??= new List<ClosedBehaviorGapModel>()).AddRange(behaviors.Gaps);
        }

        return (
            closed?.Distinct().ToArray() ?? Array.Empty<ClosedBehaviorModel>(),
            gaps?.Distinct().ToArray() ?? Array.Empty<ClosedBehaviorGapModel>());
    }

    // A referenced request's (or notification's) own module closes (or reports) the behaviors that module can see: its
    // own, and the public ones of the assemblies it references. Only the rest are this compilation's to report.
    private static bool ClosedWhereDeclared(INamedTypeSymbol target, INamedTypeSymbol behavior, Compilation compilation, CqrsKnownSymbols known)
    {
        var requestAssembly = target.ContainingAssembly;
        if (SymbolEqualityComparer.Default.Equals(requestAssembly, compilation.Assembly) || !HasGeneratedModule(requestAssembly, known))
            return false;

        if (SymbolEqualityComparer.Default.Equals(behavior.ContainingAssembly, requestAssembly)) return true;

        for (var current = behavior; current is not null; current = current.ContainingType)
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;

        return References(requestAssembly, behavior.ContainingAssembly.Name);
    }

    // [PipelineExemption] as the runtime reads it (the request's inherited attributes): the behavior's definition, or
    // exactly the behavior closed over this request.
    private static bool IsExempted(INamedTypeSymbol requestType, INamedTypeSymbol behavior, ITypeSymbol resultType, CqrsKnownSymbols known)
    {
        var exemption = known.PipelineExemptionAttribute;
        if (exemption is null) return false;

        for (var current = requestType; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            foreach (var attribute in current.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, exemption)) continue;
                if (attribute.ConstructorArguments.FirstOrDefault().Value is not INamedTypeSymbol exempted) continue;
                if (exempted.IsUnboundGenericType
                        ? SymbolEqualityComparer.Default.Equals(exempted.OriginalDefinition, behavior.OriginalDefinition)
                        : SymbolEqualityComparer.Default.Equals(exempted, behavior.Construct(requestType, resultType)))
                    return true;
            }

        return false;
    }

    // Whether closing the behavior binds a notnull type parameter to a nullable value type: the container does not
    // enforce notnull, so the behavior applies, but the compiler reports the closing (CS8714).
    private static bool RelaxesNotNull(INamedTypeSymbol open, ITypeSymbol[] arguments)
    {
        for (var i = 0; i < arguments.Length; i++)
            if (open.TypeParameters[i].HasNotNullConstraint && arguments[i].OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return true;
        return false;
    }

    // The same check the container makes before closing an open generic: every constraint of every type parameter holds
    // for the arguments (request and result, or the notification), with a constraint that names another parameter
    // (where TRequest : IRequest<TResult>) read with that parameter substituted.
    private static bool SatisfiesConstraints(INamedTypeSymbol open, ITypeSymbol[] arguments, Compilation compilation)
    {
        for (var i = 0; i < arguments.Length; i++)
        {
            var parameter = open.TypeParameters[i];
            var argument = arguments[i];
            var isNullableValue = argument.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

            if (parameter.HasReferenceTypeConstraint && !argument.IsReferenceType) return false;
            // 'struct' and 'unmanaged' take a non-nullable value type; 'unmanaged' also rules out any reference inside.
            if (parameter.HasValueTypeConstraint && (!argument.IsValueType || isNullableValue)) return false;
            if (parameter.HasUnmanagedTypeConstraint && (!argument.IsUnmanagedType || isNullableValue)) return false;
            if (parameter.HasConstructorConstraint && !argument.IsValueType &&
                !(argument is INamedTypeSymbol named && named.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)))
                return false;

            foreach (var constraint in parameter.ConstraintTypes)
            {
                // A nullable value type satisfies no interface constraint (CS0313).
                if (isNullableValue && constraint.TypeKind == TypeKind.Interface) return false;

                // Only the conversions a constraint admits: identity, implicit reference, boxing. A user-defined implicit
                // operator converts, but does not satisfy a constraint (CS0311).
                var target = SubstituteTypeParameters(constraint, open, arguments, compilation);
                var conversion = compilation.ClassifyConversion(argument, target);
                if (!(conversion.IsIdentity || (conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing)))) return false;
            }
        }

        return true;
    }

    private static ITypeSymbol SubstituteTypeParameters(ITypeSymbol type, INamedTypeSymbol open, ITypeSymbol[] arguments, Compilation compilation)
    {
        switch (type)
        {
            case ITypeParameterSymbol parameter when SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, open):
                return arguments[parameter.Ordinal];
            case INamedTypeSymbol { IsGenericType: true } named:
                var substituted = named.TypeArguments.Select(a => SubstituteTypeParameters(a, open, arguments, compilation)).ToArray();
                return named.OriginalDefinition.Construct(substituted);
            case IArrayTypeSymbol array:
                return compilation.CreateArrayTypeSymbol(SubstituteTypeParameters(array.ElementType, open, arguments, compilation), array.Rank);
            default:
                return type;
        }
    }

    // The automatic payload fingerprint of an IIdempotentRequest: a write-only rendering of its properties, minus the
    // context the dispatcher writes onto it. A request that fingerprints itself (IFingerprintedRequest) needs none.
    private static FingerprintModel? BuildFingerprint(INamedTypeSymbol type, Compilation compilation, CqrsKnownSymbols known)
    {
        var idempotent = known.IIdempotentRequest;
        if (idempotent is null || !type.AllInterfaces.Contains(idempotent, SymbolEqualityComparer.Default)) return null;

        var selfFingerprinted = known.IFingerprintedRequest;
        if (selfFingerprinted is not null && type.AllInterfaces.Contains(selfFingerprinted, SymbolEqualityComparer.Default)) return null;

        var resolver = new OutboxTypeResolver(compilation, writeOnly: true, excludeProperty: property =>
            known.IRequestContext is { } context && InheritsOrImplements(property.Type, context));

        var typeName = type.ToDisplayString(Fq);
        var reason = new StringBuilder();
        if (resolver.TryBuildObjectModel(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), out var root, reason) && root is not null)
            return new FingerprintModel(typeName, root, null);

        return new FingerprintModel(typeName, null, DiagnosticReason(reason));
    }

    // The resolver ends every reason with a period, and the CQRGEN005 / CQRGEN014 templates punctuate the reason
    // themselves ("...: {1}. Change..."), so the reason's final period is dropped rather than doubled.
    private static string DiagnosticReason(StringBuilder reason) => reason.ToString().Trim().TrimEnd('.');

    private static NotificationModel? BuildNotification(INamedTypeSymbol type, Compilation compilation, CqrsKnownSymbols known)
    {
        // A ref struct that implements INotification can never be boxed to one, so it is never published or routed.
        var notificationSymbol = known.INotification;
        if (notificationSymbol is null || type.IsRefLikeType || !type.AllInterfaces.Contains(notificationSymbol, SymbolEqualityComparer.Default))
            return null;

        var typeName = type.ToDisplayString(Fq);
        var nameAttrSymbol = known.NotificationNameAttribute;
        string? stableName = null;
        string? partitionBy = null;
        if (nameAttrSymbol is not null)
        {
            var attr = type.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass is not null &&
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, nameAttrSymbol));
            if (attr is not null && attr.ConstructorArguments.FirstOrDefault().Value is string s && !string.IsNullOrWhiteSpace(s))
                stableName = s;

            // [NotificationName("...", PartitionBy = nameof(OrderId))]: the ordering key is one property of the notification.
            if (attr is not null)
                foreach (var named in attr.NamedArguments)
                    if (named.Key == "PartitionBy" && named.Value.Value is string property && !string.IsNullOrWhiteSpace(property))
                        partitionBy = property;
        }

        if (stableName is null)
            return new NotificationModel(typeName, null, null, null, null, false, null);

        // The selector reads the property from generated code, so it must be a readable, non-static, non-indexer
        // property that generated code can see — declared on the type or inherited.
        var partitionByResolved = partitionBy is not null && GetDeclaredAndInheritedProperties(type).Any(property =>
            property.Name == partitionBy &&
            !property.IsStatic &&
            !property.IsIndexer &&
            property.GetMethod is not null &&
            GeneratedCodeAccessibility.IsAccessible(property.GetMethod, compilation));

        var resolver = new OutboxTypeResolver(compilation);
        var reason = new StringBuilder();
        if (resolver.TryBuildObjectModel(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), out var root, reason) && root is not null)
            return new NotificationModel(typeName, stableName, root, null, partitionBy, partitionByResolved, LocationInfo.CreateFrom(type));

        return new NotificationModel(typeName, stableName, null, DiagnosticReason(reason), partitionBy, partitionByResolved, LocationInfo.CreateFrom(type));
    }

    // The stable name an outbox message addresses this handler by: the [NotificationHandlerName] value, or the type's
    // namespace-qualified name. The tuple's flag reports a present-but-blank attribute (CQRGEN013) so the default is
    // used rather than an empty name.
    private static (string Name, bool IsBlank) BuildNotificationHandlerName(INamedTypeSymbol type, CqrsKnownSymbols known)
    {
        var attributeSymbol = known.NotificationHandlerNameAttribute;
        if (attributeSymbol is not null)
        {
            var attr = type.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass is not null &&
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol));
            if (attr is not null)
            {
                if (attr.ConstructorArguments.FirstOrDefault().Value is string name && !string.IsNullOrWhiteSpace(name))
                    return (name, false);

                return (DefaultNotificationHandlerName(type), true);
            }
        }

        return (DefaultNotificationHandlerName(type), false);
    }

    private static string DefaultNotificationHandlerName(INamedTypeSymbol type)
    {
        const string globalPrefix = "global::";
        var name = type.ToDisplayString(Fq);
        return name.StartsWith(globalPrefix, StringComparison.Ordinal) ? name.Substring(globalPrefix.Length) : name;
    }

    // The notification types a handler is declared for, and which of them are concrete: a concrete one gets a
    // NotificationRoute in the module, so a notification of that type is published as itself even when it is held as a
    // base type or INotification. One generated code cannot name was reported by BuildForwarders (CQRGEN010).
    private static (ITypeSymbol Type, string Name, bool IsConcrete)[] BuildHandledNotifications(INamedTypeSymbol type, Compilation compilation, CqrsKnownSymbols known)
    {
        var notificationHandlerDef = known.INotificationHandler;
        if (notificationHandlerDef is null) return Array.Empty<(ITypeSymbol, string, bool)>();

        return type.AllInterfaces
            .Where(i => i is { IsGenericType: true, TypeArguments.Length: 1 } &&
                        SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, notificationHandlerDef))
            .Select(i => i.TypeArguments[0])
            .Where(t => GeneratedCodeAccessibility.IsAccessible(t, compilation))
            .Select(t => (Type: t, Name: t.ToDisplayString(Fq), IsConcrete: IsConcreteNotification(t)))
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();
    }

    private static bool IsConcreteNotification(ITypeSymbol type)
        => type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, IsAbstract: false, IsStatic: false } &&
           !CqrsKnownSymbols.ContainsTypeParameter(type);

    private static string[] BuildContextFactories(INamedTypeSymbol type, Compilation compilation, CqrsKnownSymbols known)
    {
        var factoryInterfaceSymbol = known.IRequestContextFactory;
        if (factoryInterfaceSymbol is null) return Array.Empty<string>();

        // A generic factory type still declares which context types it can supply (the registry mapping and the CQRA011
        // marker hold for them); only the service registration, which needs a concrete type, is left to the consumer.
        // A context argument that is itself a type parameter names nothing a registry could map.
        return type.AllInterfaces
            .Where(i => i.OriginalDefinition.Equals(factoryInterfaceSymbol.OriginalDefinition, SymbolEqualityComparer.Default))
            .Select(i => i.TypeArguments.FirstOrDefault())
            .Where(t => t is not null && !CqrsKnownSymbols.ContainsTypeParameter(t) && GeneratedCodeAccessibility.IsAccessible(t, compilation))
            .Select(t => t!.ToDisplayString(Fq))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    // One hook per exception action or handler interface, closed over the response its request is dispatched with. A
    // handler declared over a wider response (legal through IRequest<out TResponse> covariance) would be registered
    // under one service type and resolved under another, so it could never run: it is reported instead (CQRGEN017).
    // Hooks over types generated code cannot name were reported by BuildForwarders.
    private static ExceptionHookModel[] BuildExceptionHooks(
        INamedTypeSymbol type,
        Compilation compilation,
        CqrsKnownSymbols known,
        List<ResultMismatchModel> mismatches)
    {
        var actionDef = known.IRequestExceptionAction2;
        var handlerDef = known.IRequestExceptionHandler3;
        if (actionDef is null || handlerDef is null) return Array.Empty<ExceptionHookModel>();

        var result = new List<ExceptionHookModel>();
        foreach (var iface in type.AllInterfaces)
        {
            if (!iface.IsGenericType) continue;

            ITypeSymbol requestType;
            ITypeSymbol exceptionType;
            ITypeSymbol? declaredResponse = null;
            ExceptionHookKind kind;

            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, actionDef))
            {
                requestType = iface.TypeArguments[0];
                exceptionType = iface.TypeArguments[1];
                kind = ExceptionHookKind.Action;
            }
            else if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, handlerDef))
            {
                requestType = iface.TypeArguments[0];
                declaredResponse = iface.TypeArguments[1];
                exceptionType = iface.TypeArguments[2];
                kind = ExceptionHookKind.Handler;
            }
            else
            {
                continue;
            }

            if (!iface.TypeArguments.All(a => GeneratedCodeAccessibility.IsAccessible(a, compilation))) continue;
            if (known.ShapeOf(requestType) is not { } shape) continue;

            if (declaredResponse is not null && !SymbolEqualityComparer.Default.Equals(declaredResponse, shape.Response))
            {
                mismatches.Add(new ResultMismatchModel(
                    iface.ToDisplayString(),
                    requestType.ToDisplayString(),
                    declaredResponse.ToDisplayString(),
                    shape.Response.ToDisplayString()));
                continue;
            }

            if (!GeneratedCodeAccessibility.IsAccessible(shape.Response, compilation)) continue;

            result.Add(new ExceptionHookModel(
                requestType.ToDisplayString(Fq),
                exceptionType.ToDisplayString(Fq),
                shape.Response.ToDisplayString(Fq),
                kind));
        }

        return result.ToArray();
    }
}
