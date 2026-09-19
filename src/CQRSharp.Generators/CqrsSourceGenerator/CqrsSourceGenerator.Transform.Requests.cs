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
                new EquatableArray<string>(attr.ConstructorArguments.Select(RenderTypedConstant).ToArray()),
                // [Audit(Category = "billing")]: re-applied through an object initializer on the rebuilt instance.
                new EquatableArray<string>(attr.NamedArguments
                    .Select(named => $"{EscapeIdentifier(named.Key)} = {RenderTypedConstant(named.Value)}")
                    .ToArray())))
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
}
