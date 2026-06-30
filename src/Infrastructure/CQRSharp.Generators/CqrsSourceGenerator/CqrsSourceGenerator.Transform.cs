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

        // Drop types that are not CQRSharp-relevant in any way, to keep the collected set small.
        if (!implementsAnyHandler &&
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
            aotBehavior);
    }

    private static HandlerImplModel[] BuildHandlerImpls(INamedTypeSymbol type, Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);
        var commandHandlerDef = known.ICommandHandler2;
        var resultCommandHandlerDef = known.IResultCommandHandler3;
        var commandResultGenericDef = known.CommandResultGeneric;
        var queryHandlerDef = known.IQueryHandler3;
        var streamHandlerDef = known.IStreamRequestHandler3;
        var asyncEnumerableDef = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1");
        if (commandHandlerDef is null || queryHandlerDef is null) return Array.Empty<HandlerImplModel>();

        var implName = type.ToDisplayString(Fq);
        var result = new List<HandlerImplModel>();

        foreach (var iface in GetInterfacesAndBaseInterfaces(type))
        {
            if (!iface.IsGenericType) continue;
            var def = iface.OriginalDefinition;

            if (SymbolEqualityComparer.Default.Equals(def, commandHandlerDef))
            {
                if (iface.TypeArguments.Length < 2) continue;
                var requestType = iface.TypeArguments[0];
                var contextType = iface.TypeArguments[1];
                if (!IsAccessibleFromGeneratedCode(requestType) || !IsAccessibleFromGeneratedCode(contextType)) continue;
                result.Add(new HandlerImplModel(implName, HandlerKind.Command, requestType.ToDisplayString(Fq), null,
                    iface.ToDisplayString(FqNullable), iface.ToDisplayString(Fq), BuildRequestMetadata(requestType, compilation)));
            }
            else if (SymbolEqualityComparer.Default.Equals(def, queryHandlerDef))
            {
                if (iface.TypeArguments.Length < 3) continue;
                var requestType = iface.TypeArguments[0];
                var resultType = iface.TypeArguments[1];
                var contextType = iface.TypeArguments[2];
                if (!IsAccessibleFromGeneratedCode(requestType) || !IsAccessibleFromGeneratedCode(resultType) ||
                    !IsAccessibleFromGeneratedCode(contextType)) continue;
                result.Add(new HandlerImplModel(implName, HandlerKind.Query, requestType.ToDisplayString(Fq),
                    resultType.ToDisplayString(Fq), iface.ToDisplayString(FqNullable), iface.ToDisplayString(Fq),
                    BuildRequestMetadata(requestType, compilation)));
            }
            else if (resultCommandHandlerDef is not null && commandResultGenericDef is not null &&
                     SymbolEqualityComparer.Default.Equals(def, resultCommandHandlerDef))
            {
                if (iface.TypeArguments.Length < 3) continue;
                var requestType = iface.TypeArguments[0];
                var valueType = iface.TypeArguments[1];
                var contextType = iface.TypeArguments[2];
                if (!IsAccessibleFromGeneratedCode(requestType) || !IsAccessibleFromGeneratedCode(valueType) ||
                    !IsAccessibleFromGeneratedCode(contextType)) continue;
                // A value-returning command dispatches like a query whose result is CommandResult<TValue>.
                var resultType = commandResultGenericDef.Construct(valueType);
                result.Add(new HandlerImplModel(implName, HandlerKind.Query, requestType.ToDisplayString(Fq),
                    resultType.ToDisplayString(Fq), iface.ToDisplayString(FqNullable), iface.ToDisplayString(Fq),
                    BuildRequestMetadata(requestType, compilation)));
            }
            else if (streamHandlerDef is not null && asyncEnumerableDef is not null &&
                     SymbolEqualityComparer.Default.Equals(def, streamHandlerDef))
            {
                if (iface.TypeArguments.Length < 3) continue;
                var requestType = iface.TypeArguments[0];
                var itemType = iface.TypeArguments[1];
                var contextType = iface.TypeArguments[2];
                if (!IsAccessibleFromGeneratedCode(requestType) || !IsAccessibleFromGeneratedCode(itemType) ||
                    !IsAccessibleFromGeneratedCode(contextType)) continue;
                var streamResult = asyncEnumerableDef.Construct(itemType);
                result.Add(new HandlerImplModel(implName, HandlerKind.Stream, requestType.ToDisplayString(Fq),
                    streamResult.ToDisplayString(Fq), iface.ToDisplayString(FqNullable), iface.ToDisplayString(Fq),
                    BuildRequestMetadata(requestType, compilation)));
            }
        }

        return result.ToArray();
    }

    // The request registry reads pre/post/exemption attributes and the context type from the request type itself,
    // via the handler binding, so it is captured here (and works for request types in referenced assemblies too).
    private static RequestMetadataModel BuildRequestMetadata(ITypeSymbol requestType, Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);

        var defaultContext = known.RequestContextBase;
        var contextSymbol = GetRequestContextType(requestType, compilation) ?? defaultContext;
        if (contextSymbol is null || !IsAccessibleFromGeneratedCode(contextSymbol))
            contextSymbol = defaultContext;
        var contextName = contextSymbol?.ToDisplayString(Fq) ?? "global::System.Object";

        return new RequestMetadataModel(
            contextName,
            new EquatableArray<AttributeModel>(BuildAttributeModels(requestType, known.IPreHandlerAttribute)),
            new EquatableArray<AttributeModel>(BuildAttributeModels(requestType, known.IPostHandlerAttribute)),
            new EquatableArray<AttributeModel>(BuildAttributeModels(requestType, known.PipelineExemptionAttribute)));
    }

    private static RequestModel? BuildRequest(INamedTypeSymbol type, Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);
        var commandSymbol = known.ICommand;
        var querySymbol = known.IQuery;
        var streamRequestSymbol = known.IStreamRequest;
        var commandWithResultSymbol = known.ICommandWithResult;
        var commandResultGenericDef = known.CommandResultGeneric;
        if (commandSymbol is null || querySymbol is null) return null;

        var streamInterface = streamRequestSymbol is null
            ? null
            : type.AllInterfaces.FirstOrDefault(i =>
                i.IsGenericType && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, streamRequestSymbol));
        var queryInterface = type.AllInterfaces.FirstOrDefault(i =>
            i.IsGenericType && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, querySymbol));
        var resultCommandInterface = commandWithResultSymbol is null
            ? null
            : type.AllInterfaces.FirstOrDefault(i =>
                i.IsGenericType && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, commandWithResultSymbol));
        var isCommand = type.AllInterfaces.Contains(commandSymbol, SymbolEqualityComparer.Default);

        RequestKind kind;
        string? resultOrItemNullable = null;
        string? queryResultNonNullable = null;
        if (streamInterface is not null)
        {
            kind = RequestKind.Stream;
            resultOrItemNullable = streamInterface.TypeArguments[0].ToDisplayString(FqNullable);
        }
        else if (queryInterface is not null)
        {
            kind = RequestKind.Query;
            resultOrItemNullable = queryInterface.TypeArguments[0].ToDisplayString(FqNullable);
            queryResultNonNullable = queryInterface.TypeArguments[0].ToDisplayString(Fq);
        }
        else if (resultCommandInterface is not null && commandResultGenericDef is not null)
        {
            // A value-returning command (ICommand<TValue>) dispatches like a query whose result is CommandResult<TValue>.
            kind = RequestKind.Query;
            var commandResult = commandResultGenericDef.Construct(resultCommandInterface.TypeArguments[0]);
            resultOrItemNullable = commandResult.ToDisplayString(FqNullable);
            queryResultNonNullable = commandResult.ToDisplayString(Fq);
        }
        else if (isCommand)
        {
            kind = RequestKind.Command;
        }
        else
        {
            return null;
        }

        return new RequestModel(
            kind,
            type.ToDisplayString(Fq),
            type.ToDisplayString(FqNullable),
            resultOrItemNullable,
            queryResultNonNullable);
    }

    private static AttributeModel[] BuildAttributeModels(ITypeSymbol requestType, INamedTypeSymbol? attributeInterfaceSymbol)
    {
        if (attributeInterfaceSymbol is null) return Array.Empty<AttributeModel>();

        return requestType.GetAttributes()
            .Where(attr => attr.AttributeClass != null &&
                           IsAccessibleFromGeneratedCode(attr.AttributeClass) &&
                           InheritsOrImplements(attr.AttributeClass, attributeInterfaceSymbol))
            .Select(attr => new AttributeModel(
                attr.AttributeClass!.ToDisplayString(Fq),
                new EquatableArray<string>(attr.ConstructorArguments.Select(RenderTypedConstant).ToArray())))
            .ToArray();
    }

    private static NotificationModel? BuildNotification(INamedTypeSymbol type, Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);
        var notificationSymbol = known.INotification;
        if (notificationSymbol is null || !type.AllInterfaces.Contains(notificationSymbol, SymbolEqualityComparer.Default))
            return null;

        var typeName = type.ToDisplayString(Fq);
        var nameAttrSymbol = known.NotificationNameAttribute;
        string? stableName = null;
        if (nameAttrSymbol is not null)
        {
            var attr = type.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass is not null &&
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, nameAttrSymbol));
            if (attr is not null && attr.ConstructorArguments.FirstOrDefault().Value is string s && !string.IsNullOrWhiteSpace(s))
                stableName = s;
        }

        if (stableName is null)
            return new NotificationModel(typeName, null, null, null, null);

        var resolver = new OutboxTypeResolver(compilation);
        var reason = new StringBuilder();
        if (resolver.TryBuildObjectModel(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), out var root, reason) && root is not null)
            return new NotificationModel(typeName, stableName, root, null, null);

        return new NotificationModel(typeName, stableName, null, reason.ToString().Trim(), LocationInfo.CreateFrom(type));
    }

    private static string[] BuildHandledNotifications(INamedTypeSymbol type, Compilation compilation)
    {
        var notificationHandlerDef = CqrsKnownSymbols.For(compilation).INotificationHandler;
        if (notificationHandlerDef is null) return Array.Empty<string>();

        return GetInterfacesAndBaseInterfaces(type)
            .Where(i => i is { IsGenericType: true, TypeArguments.Length: 1 } &&
                        SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, notificationHandlerDef))
            .Select(i => i.TypeArguments[0])
            .Where(t => t is not null && IsAccessibleFromGeneratedCode(t))
            .Select(t => t.ToDisplayString(Fq))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] BuildContextFactories(INamedTypeSymbol type, Compilation compilation)
    {
        var factoryInterfaceSymbol = CqrsKnownSymbols.For(compilation).IRequestContextFactory;
        if (factoryInterfaceSymbol is null) return Array.Empty<string>();

        return GetInterfacesAndBaseInterfaces(type)
            .Where(i => i.OriginalDefinition.Equals(factoryInterfaceSymbol.OriginalDefinition, SymbolEqualityComparer.Default))
            .Select(i => i.TypeArguments.FirstOrDefault())
            .Where(t => t is not null && IsAccessibleFromGeneratedCode(t))
            .Select(t => t!.ToDisplayString(Fq))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static ExceptionHookModel[] BuildExceptionHooks(INamedTypeSymbol type, Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);
        var actionDef = known.IRequestExceptionAction2;
        var handlerDef = known.IRequestExceptionHandler3;
        var systemException = compilation.GetTypeByMetadataName("System.Exception");
        if (actionDef is null || handlerDef is null || systemException is null) return Array.Empty<ExceptionHookModel>();

        var result = new List<ExceptionHookModel>();
        foreach (var iface in GetInterfacesAndBaseInterfaces(type))
        {
            if (!iface.IsGenericType) continue;

            ITypeSymbol? requestType = null;
            ITypeSymbol? exceptionType = null;
            ExceptionHookKind kind;

            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, actionDef) && iface.TypeArguments.Length >= 2)
            {
                requestType = iface.TypeArguments[0];
                exceptionType = iface.TypeArguments[1];
                kind = ExceptionHookKind.Action;
            }
            else if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, handlerDef) && iface.TypeArguments.Length >= 3)
            {
                requestType = iface.TypeArguments[0];
                exceptionType = iface.TypeArguments[2];
                kind = ExceptionHookKind.Handler;
            }
            else
            {
                continue;
            }

            if (!IsAccessibleFromGeneratedCode(requestType) || !IsAccessibleFromGeneratedCode(exceptionType) ||
                !IsExceptionType(exceptionType, systemException))
                continue;

            var resultTypeName = ResolveRequestResultTypeName(requestType, compilation);
            if (resultTypeName is null) continue;

            result.Add(new ExceptionHookModel(
                requestType.ToDisplayString(Fq),
                exceptionType.ToDisplayString(Fq),
                resultTypeName,
                GetInheritanceDepth(exceptionType),
                kind));
        }

        return result.ToArray();
    }

    private static string? ResolveRequestResultTypeName(ITypeSymbol requestType, Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);
        var commandSymbol = known.ICommand;
        var querySymbol = known.IQuery;
        var streamRequestSymbol = known.IStreamRequest;
        var commandResultSymbol = known.CommandResult;
        var commandWithResultSymbol = known.ICommandWithResult;
        var commandResultGenericDef = known.CommandResultGeneric;
        var asyncEnumerableSymbol = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1");
        if (commandSymbol is null || querySymbol is null || commandResultSymbol is null) return null;
        if (requestType is not INamedTypeSymbol requestSymbol) return null;

        var queryInterface = requestSymbol.AllInterfaces.FirstOrDefault(i =>
            i.IsGenericType && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, querySymbol));
        var streamInterface = streamRequestSymbol is null
            ? null
            : requestSymbol.AllInterfaces.FirstOrDefault(i =>
                i.IsGenericType && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, streamRequestSymbol));
        var resultCommandInterface = commandWithResultSymbol is null
            ? null
            : requestSymbol.AllInterfaces.FirstOrDefault(i =>
                i.IsGenericType && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, commandWithResultSymbol));
        var isCommand = requestSymbol.AllInterfaces.Contains(commandSymbol, SymbolEqualityComparer.Default);
        if (!isCommand && queryInterface is null && streamInterface is null && resultCommandInterface is null) return null;

        ITypeSymbol resultType;
        if (streamInterface is not null)
        {
            if (asyncEnumerableSymbol is null) return null;
            resultType = asyncEnumerableSymbol.Construct(streamInterface.TypeArguments[0]);
        }
        else if (resultCommandInterface is not null && commandResultGenericDef is not null)
        {
            resultType = commandResultGenericDef.Construct(resultCommandInterface.TypeArguments[0]);
        }
        else
        {
            resultType = (ITypeSymbol)(queryInterface?.TypeArguments[0] ?? commandResultSymbol);
        }

        return IsAccessibleFromGeneratedCode(resultType) ? resultType.ToDisplayString(Fq) : null;
    }

    private static string? BuildAotOpenGenericBehavior(INamedTypeSymbol type, Compilation compilation, bool isAccessible)
    {
        var pipelineBehaviorSymbol = CqrsKnownSymbols.For(compilation).IPipelineBehavior;
        if (pipelineBehaviorSymbol is null) return null;
        if (type is not { IsAbstract: false, IsGenericType: true } || !isAccessible) return null;
        if (!type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, pipelineBehaviorSymbol)))
            return null;

        return type.ToDisplayString(Fq).Split('<')[0];
    }

    private static bool IsExceptionType(ITypeSymbol exceptionType, INamedTypeSymbol systemExceptionSymbol)
    {
        for (var current = exceptionType; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, systemExceptionSymbol.OriginalDefinition))
                return true;
        return false;
    }

    private static int GetInheritanceDepth(ITypeSymbol type)
    {
        var depth = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            depth++;
        return depth;
    }

    // ============================================================================================================
    // Symbol helpers used only during extraction.
    // ============================================================================================================

    private static bool IsAccessibleFromGeneratedCode(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol namedTypeSymbol) return false;
        for (var current = namedTypeSymbol; current is not null; current = current.ContainingType)
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                return false;
        return true;
    }

    private static bool IsAccessibleFromGeneratedCode(IMethodSymbol methodSymbol)
    {
        if (methodSymbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            return false;
        return IsAccessibleFromGeneratedCode(methodSymbol.ContainingType);
    }

    private static IEnumerable<INamedTypeSymbol> GetInterfacesAndBaseInterfaces(ITypeSymbol typeSymbol)
    {
        var allInterfaces = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var typesToProcess = new Queue<ITypeSymbol>();
        typesToProcess.Enqueue(typeSymbol);

        while (typesToProcess.Count > 0)
        {
            var currentType = typesToProcess.Dequeue();
            if (currentType is null) continue;

            foreach (var iface in currentType.Interfaces.Where(iface => allInterfaces.Add(iface)))
                typesToProcess.Enqueue(iface);

            if (currentType.BaseType != null)
                typesToProcess.Enqueue(currentType.BaseType);
        }

        return allInterfaces;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllKnownHandlerSymbols(Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);
        var commandHandlerSymbols = new[] { known.ICommandHandler1, known.ICommandHandler2 };
        var resultCommandHandlerSymbols = new[] { known.IResultCommandHandler2, known.IResultCommandHandler3 };
        var queryHandlerSymbols = new[] { known.IQueryHandler2, known.IQueryHandler3 };
        var streamHandlerSymbols = new[] { known.IStreamRequestHandler2, known.IStreamRequestHandler3 };

        return commandHandlerSymbols
            .Concat(resultCommandHandlerSymbols)
            .Concat(queryHandlerSymbols)
            .Concat(streamHandlerSymbols)
            .Concat(new[]
            {
                known.INotificationHandler,
                known.IRequestValidator1,
                known.IRequestExceptionHandler3,
                known.IRequestExceptionAction2,
                known.IPipelineBehavior,
                known.IStreamPipelineBehavior
            })
            .Where(s => s is not null)
            .Cast<INamedTypeSymbol>();
    }

    private static ITypeSymbol? GetRequestContextType(ITypeSymbol requestTypeSymbol, Compilation compilation)
    {
        var requestBaseSymbol = CqrsKnownSymbols.For(compilation).RequestBaseGeneric;
        if (requestBaseSymbol is null) return null;

        var current = requestTypeSymbol;
        while (current != null)
        {
            if (current is INamedTypeSymbol { IsGenericType: true } namedType &&
                SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, requestBaseSymbol))
                return namedType.TypeArguments[0];

            current = current.BaseType;
        }

        return null;
    }

    private static bool InheritsOrImplements(ITypeSymbol type, ITypeSymbol baseType)
    {
        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, baseType.OriginalDefinition))
            return true;

        return GetInterfacesAndBaseInterfaces(type).Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, baseType.OriginalDefinition)) ||
               (type.BaseType != null && InheritsOrImplements(type.BaseType, baseType));
    }

    private static string RenderTypedConstant(TypedConstant constant)
    {
        if (constant.IsNull) return "null";
        var value = constant.Value;

        switch (constant.Kind)
        {
            case TypedConstantKind.Primitive:
                return value switch
                {
                    string s => $"\"{s.Replace("\"", "\\\"")}\"",
                    bool b => b.ToString().ToLowerInvariant(),
                    _ => value?.ToString() ?? "null"
                };
            case TypedConstantKind.Enum:
                if (constant.Type is not INamedTypeSymbol enumType)
                    return $"({constant.Type!.ToDisplayString(Fq)}){value}";
                var member = enumType.GetMembers().OfType<IFieldSymbol>()
                    .FirstOrDefault(f => f.ConstantValue is not null && f.ConstantValue.Equals(value));
                return member is not null
                    ? $"{enumType.ToDisplayString(Fq)}.{member.Name}"
                    : $"({enumType.ToDisplayString(Fq)}){value}";
            case TypedConstantKind.Type:
                return $"typeof({((ITypeSymbol)value!).ToDisplayString(Fq)})";
            case TypedConstantKind.Error:
            case TypedConstantKind.Array:
            default:
                return "null";
        }
    }

    private static string GetJsonPropertyName(IPropertySymbol property, INamedTypeSymbol? jsonPropertyNameAttributeSymbol)
    {
        if (jsonPropertyNameAttributeSymbol is not null)
        {
            var attr = property.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass is not null &&
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, jsonPropertyNameAttributeSymbol));
            if (attr is not null)
            {
                var arg = attr.ConstructorArguments.FirstOrDefault();
                if (arg.Value is string name && !string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }

        return ToCamelCase(property.Name);
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (!char.IsUpper(name[0])) return name;

        var chars = name.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
                break;

            var hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                break;

            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }

    private static string CreateIdentifierSuffix(string text, string prefix)
    {
        var hash = StableNameHash(text);

        var sb = new StringBuilder();
        sb.Append(prefix);

        var maxLen = Math.Min(text.Length, 40);
        for (var i = 0; i < maxLen; i++)
        {
            var c = text[i];
            if ((c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }

        sb.Append('_');
        sb.Append(hash.ToString("X8"));
        return sb.ToString();
    }

    private static uint StableNameHash(string stableName)
    {
        // FNV-1a 32-bit
        unchecked
        {
            var hash = 2166136261u;
            for (var i = 0; i < stableName.Length; i++)
            {
                hash ^= stableName[i];
                hash *= 16777619u;
            }

            return hash;
        }
    }

    /// <summary>
    ///     Builds the recursive outbox object/value graph for a notification type. Runs in the transform (it needs
    ///     symbols); the emitter consumes the resulting equatable records. STJ source-gen cannot be used here — see
    ///     <c>CqrsSourceGenerator.OutboxSerialization.cs</c>.
    /// </summary>
    private sealed class OutboxTypeResolver
    {
        private readonly INamedTypeSymbol? _guid;
        private readonly INamedTypeSymbol? _dateTime;
        private readonly INamedTypeSymbol? _dateTimeOffset;
        private readonly INamedTypeSymbol? _timeSpan;
        private readonly INamedTypeSymbol? _nullable;
        private readonly INamedTypeSymbol? _jsonPropertyName;
        private readonly INamedTypeSymbol? _jsonIgnore;
        private readonly INamedTypeSymbol? _ienumerableOfT;
        private readonly INamedTypeSymbol?[] _listLikeDefs;

        private readonly Dictionary<ITypeSymbol, OutboxObjectModel> _objectCache =
            new(SymbolEqualityComparer.Default);

        public OutboxTypeResolver(Compilation compilation)
        {
            _guid = compilation.GetTypeByMetadataName("System.Guid");
            _dateTime = compilation.GetTypeByMetadataName("System.DateTime");
            _dateTimeOffset = compilation.GetTypeByMetadataName("System.DateTimeOffset");
            _timeSpan = compilation.GetTypeByMetadataName("System.TimeSpan");
            _nullable = compilation.GetTypeByMetadataName("System.Nullable`1");
            _jsonPropertyName = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyNameAttribute");
            _jsonIgnore = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");
            _ienumerableOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
            _listLikeDefs = new[]
            {
                compilation.GetTypeByMetadataName("System.Collections.Generic.List`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.IList`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.ICollection`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyCollection`1"),
                compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1")
            };
        }

        public bool TryBuildObjectModel(
            INamedTypeSymbol type,
            HashSet<ITypeSymbol> inProgress,
            out OutboxObjectModel? model,
            StringBuilder reason)
        {
            if (_objectCache.TryGetValue(type, out var cached))
            {
                model = cached;
                return true;
            }

            model = null;

            if (inProgress.Contains(type))
            {
                reason.Append($"circular reference through '{type.ToDisplayString(Fq)}' is not supported. ");
                return false;
            }

            inProgress.Add(type);
            try
            {
                var properties = type.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(p =>
                        !p.IsStatic &&
                        !p.IsIndexer &&
                        p.GetMethod is not null &&
                        IsAccessibleFromGeneratedCode(p.GetMethod) &&
                        p.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
                    .Where(p =>
                        _jsonIgnore is null ||
                        !p.GetAttributes().Any(a => a.AttributeClass is not null &&
                                                    SymbolEqualityComparer.Default.Equals(a.AttributeClass, _jsonIgnore)))
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToArray();

                var members = new OutboxMemberModel[properties.Length];

                for (var i = 0; i < properties.Length; i++)
                {
                    var property = properties[i];
                    var propTypeName = property.Type.ToDisplayString(Fq);

                    if (!TryBuildValueModel(property.Type, inProgress, out var value, reason) || value is null)
                    {
                        reason.Append($"Unsupported property '{property.Name}' of type '{propTypeName}'. ");
                        return false;
                    }

                    var canInitialize = property.SetMethod is not null && IsAccessibleFromGeneratedCode(property.SetMethod);

                    members[i] = new OutboxMemberModel(
                        property.Name,
                        GetJsonPropertyName(property, _jsonPropertyName),
                        "__" + property.Name,
                        value,
                        canInitialize);
                }

                if (!TrySelectConstructor(type, members, out var constructorExpression, reason))
                    return false;

                var helperId = CreateIdentifierSuffix(type.ToDisplayString(Fq), "T_");
                var built = new OutboxObjectModel(
                    type.ToDisplayString(Fq),
                    new EquatableArray<OutboxMemberModel>(members),
                    constructorExpression,
                    helperId);

                _objectCache[type] = built;
                model = built;
                return true;
            }
            finally
            {
                inProgress.Remove(type);
            }
        }

        public bool TryBuildValueModel(
            ITypeSymbol type,
            HashSet<ITypeSymbol> inProgress,
            out OutboxValueModel? model,
            StringBuilder reason)
        {
            model = null;

            var localTypeName = type.ToDisplayString(Fq);
            var isNullableValueType = false;
            var underlying = type;

            if (_nullable is not null &&
                type is INamedTypeSymbol named &&
                named.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _nullable) &&
                named.TypeArguments.Length == 1)
            {
                isNullableValueType = true;
                underlying = named.TypeArguments[0];
            }

            var nonNullableTypeName = underlying.ToDisplayString(Fq);
            var isNullableReferenceType = !isNullableValueType && type.NullableAnnotation == NullableAnnotation.Annotated;

            if (TryGetScalarKind(underlying, out var scalarKind))
            {
                model = OutboxValueModel.Scalar(scalarKind, localTypeName, nonNullableTypeName, isNullableValueType, isNullableReferenceType);
                return true;
            }

            if (underlying.TypeKind == TypeKind.Enum && underlying is INamedTypeSymbol enumType)
            {
                var enumUnderlying = enumType.EnumUnderlyingType?.ToDisplayString(Fq);
                if (enumUnderlying is null)
                {
                    reason.Append($"enum '{nonNullableTypeName}' has no resolvable underlying type. ");
                    return false;
                }

                model = OutboxValueModel.Enum(localTypeName, nonNullableTypeName, enumUnderlying, isNullableValueType);
                return true;
            }

            if (type is IArrayTypeSymbol array)
            {
                if (array.Rank != 1)
                {
                    reason.Append($"multi-dimensional array '{localTypeName}' is not supported. ");
                    return false;
                }

                if (!TryBuildValueModel(array.ElementType, inProgress, out var elem, reason) || elem is null)
                    return false;

                model = OutboxValueModel.Collection(localTypeName, elem, true);
                return true;
            }

            if (underlying is INamedTypeSymbol generic &&
                generic.IsGenericType &&
                generic.TypeArguments.Length == 1 &&
                _listLikeDefs.Any(d => d is not null && SymbolEqualityComparer.Default.Equals(d, generic.OriginalDefinition)))
            {
                if (!TryBuildValueModel(generic.TypeArguments[0], inProgress, out var elem, reason) || elem is null)
                    return false;

                model = OutboxValueModel.Collection(localTypeName, elem, false);
                return true;
            }

            if (ImplementsEnumerableOfT(underlying))
            {
                reason.Append($"collection type '{nonNullableTypeName}' is not supported (only arrays and List/IList/IReadOnlyList/ICollection/IReadOnlyCollection/IEnumerable of a supported element type). ");
                return false;
            }

            if (underlying is INamedTypeSymbol obj && IsEligibleNestedObject(obj))
            {
                if (!TryBuildObjectModel(obj, inProgress, out var nested, reason) || nested is null)
                    return false;

                model = OutboxValueModel.Object(localTypeName, nested, isNullableValueType, obj.IsReferenceType);
                return true;
            }

            reason.Append($"type '{nonNullableTypeName}' is not a supported scalar, enum, array, list, or serializable object. ");
            return false;
        }

        private bool TrySelectConstructor(
            INamedTypeSymbol type,
            OutboxMemberModel[] members,
            out string constructorExpression,
            StringBuilder reason)
        {
            constructorExpression = string.Empty;
            var typeName = type.ToDisplayString(Fq);

            var parameterlessCtor = type.InstanceConstructors.FirstOrDefault(c =>
                c.Parameters.Length == 0 && IsAccessibleFromGeneratedCode(c));

            var hasNonInitializable = members.Any(m => !m.CanInitialize);

            if (parameterlessCtor is not null && !hasNonInitializable)
            {
                constructorExpression = $"new {typeName}()";
                return true;
            }

            var candidates = type.InstanceConstructors
                .Where(c => c.Parameters.Length > 0 && IsAccessibleFromGeneratedCode(c))
                .Select(c => (Ctor: c, Map: TryMapConstructor(members, c, out var map) ? map : null))
                .Where(x => x.Map is not null)
                .Select(x => (x.Ctor, Map: x.Map!))
                .OrderByDescending(x => x.Ctor.Parameters.Length)
                .ToArray();

            if (candidates.Length == 0)
            {
                reason.Append("no accessible parameterless constructor and no accessible constructor with parameters matching its serializable properties. ");
                return false;
            }

            var bestParamCount = candidates[0].Ctor.Parameters.Length;
            var best = candidates.Where(c => c.Ctor.Parameters.Length == bestParamCount).ToArray();
            if (best.Length != 1)
            {
                reason.Append("multiple eligible constructors found; ensure a single unambiguous constructor (or add a parameterless constructor). ");
                return false;
            }

            var ctorIndices = best[0].Map;
            var ctorLocalNames = new string[ctorIndices.Length];
            for (var p = 0; p < ctorIndices.Length; p++)
            {
                var index = ctorIndices[p];
                members[index] = members[index] with { IsConstructorParameter = true };
                ctorLocalNames[p] = members[index].LocalName;
            }

            foreach (var member in members)
            {
                if (member.CanInitialize || member.IsConstructorParameter) continue;
                reason.Append($"property '{member.PropertyName}' is not settable and is not provided by the selected constructor. ");
                return false;
            }

            constructorExpression = $"new {typeName}({string.Join(", ", ctorLocalNames)})";
            return true;
        }

        private static bool TryMapConstructor(OutboxMemberModel[] members, IMethodSymbol ctor, out int[] propertyIndices)
        {
            propertyIndices = Array.Empty<int>();
            if (ctor.Parameters.Length == 0) return false;

            var map = new int[ctor.Parameters.Length];

            for (var i = 0; i < ctor.Parameters.Length; i++)
            {
                var param = ctor.Parameters[i];
                var paramTypeName = param.Type.ToDisplayString(Fq);
                if (paramTypeName.EndsWith("?", StringComparison.Ordinal))
                    paramTypeName = paramTypeName.Substring(0, paramTypeName.Length - 1);

                var found = -1;
                for (var p = 0; p < members.Length; p++)
                {
                    var member = members[p];
                    if (!string.Equals(member.JsonName, param.Name, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(member.PropertyName, param.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var memberTypeName = member.Value.LocalTypeName;
                    if (memberTypeName.EndsWith("?", StringComparison.Ordinal))
                        memberTypeName = memberTypeName.Substring(0, memberTypeName.Length - 1);

                    if (!string.Equals(memberTypeName, paramTypeName, StringComparison.Ordinal))
                        continue;

                    found = p;
                    break;
                }

                if (found < 0) return false;
                map[i] = found;
            }

            if (map.Distinct().Count() != map.Length) return false;

            propertyIndices = map;
            return true;
        }

        private bool TryGetScalarKind(ITypeSymbol type, out OutboxScalarKind kind)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_String: kind = OutboxScalarKind.String; return true;
                case SpecialType.System_Boolean: kind = OutboxScalarKind.Boolean; return true;
                case SpecialType.System_Byte: kind = OutboxScalarKind.Byte; return true;
                case SpecialType.System_SByte: kind = OutboxScalarKind.SByte; return true;
                case SpecialType.System_Int16: kind = OutboxScalarKind.Int16; return true;
                case SpecialType.System_UInt16: kind = OutboxScalarKind.UInt16; return true;
                case SpecialType.System_Int32: kind = OutboxScalarKind.Int32; return true;
                case SpecialType.System_UInt32: kind = OutboxScalarKind.UInt32; return true;
                case SpecialType.System_Int64: kind = OutboxScalarKind.Int64; return true;
                case SpecialType.System_UInt64: kind = OutboxScalarKind.UInt64; return true;
                case SpecialType.System_Single: kind = OutboxScalarKind.Single; return true;
                case SpecialType.System_Double: kind = OutboxScalarKind.Double; return true;
                case SpecialType.System_Decimal: kind = OutboxScalarKind.Decimal; return true;
            }

            if (_guid is not null && SymbolEqualityComparer.Default.Equals(type, _guid)) { kind = OutboxScalarKind.Guid; return true; }
            if (_dateTime is not null && SymbolEqualityComparer.Default.Equals(type, _dateTime)) { kind = OutboxScalarKind.DateTime; return true; }
            if (_dateTimeOffset is not null && SymbolEqualityComparer.Default.Equals(type, _dateTimeOffset)) { kind = OutboxScalarKind.DateTimeOffset; return true; }
            if (_timeSpan is not null && SymbolEqualityComparer.Default.Equals(type, _timeSpan)) { kind = OutboxScalarKind.TimeSpan; return true; }

            kind = default;
            return false;
        }

        private bool ImplementsEnumerableOfT(ITypeSymbol type)
        {
            if (_ienumerableOfT is null) return false;
            if (type is INamedTypeSymbol nt && nt.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(nt.OriginalDefinition, _ienumerableOfT))
                return true;
            return type.AllInterfaces.Any(i =>
                i.IsGenericType && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, _ienumerableOfT));
        }

        private static bool IsEligibleNestedObject(INamedTypeSymbol type)
        {
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
            if (type.IsAbstract) return false;
            if (type.IsTupleType) return false;
            if (type.SpecialType == SpecialType.System_Object) return false;
            if (!IsAccessibleFromGeneratedCode(type)) return false;

            var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ||
                ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal))
                return false;

            return true;
        }
    }
}
