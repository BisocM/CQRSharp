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
    // CQRGEN010 helper: returns the type arguments of this handler's dispatch-handler interfaces (request/result/context/
    // notification) that fail the generated-code accessibility test — exactly the args whose presence makes
    // BuildHandlerImpls skip the binding. Mirrors that drop condition so the diagnostic and the drop stay in lock-step.
    private static string[] CollectInaccessibleBoundTypes(INamedTypeSymbol type, Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);
        var dispatchHandlerDefs = new[]
        {
            known.ICommandHandler1, known.ICommandHandler2,
            known.IResultCommandHandler2, known.IResultCommandHandler3,
            known.IQueryHandler2, known.IQueryHandler3,
            known.IStreamRequestHandler2, known.IStreamRequestHandler3,
            known.INotificationHandler
        };

        List<string>? inaccessible = null;
        foreach (var iface in GetInterfacesAndBaseInterfaces(type))
        {
            if (!iface.IsGenericType) continue;
            if (!dispatchHandlerDefs.Any(def => def is not null &&
                    SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, def)))
                continue;

            foreach (var typeArg in iface.TypeArguments)
                if (!IsAccessibleFromGeneratedCode(typeArg))
                    (inaccessible ??= new List<string>()).Add(typeArg.ToDisplayString(Fq));
        }

        return inaccessible is null
            ? Array.Empty<string>()
            : inaccessible.Distinct(StringComparer.Ordinal).ToArray();
    }

    // True when the type implements one of the core dispatch-handler interfaces (command/query/result-command/stream/
    // notification) — the ones the generator registers and that fail with a runtime "no handler" if unregistered.
    // Deliberately excludes pipeline behaviors (supported as open generics) and validators/exception hooks.
    private static bool ImplementsDispatchHandlerInterface(INamedTypeSymbol type, Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);
        var dispatchHandlerDefs = new[]
        {
            known.ICommandHandler1, known.ICommandHandler2,
            known.IResultCommandHandler2, known.IResultCommandHandler3,
            known.IQueryHandler2, known.IQueryHandler3,
            known.IStreamRequestHandler2, known.IStreamRequestHandler3,
            known.INotificationHandler
        };

        return GetInterfacesAndBaseInterfaces(type).Any(iface =>
            iface.IsGenericType &&
            dispatchHandlerDefs.Any(def => def is not null &&
                                           SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, def)));
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
}
