using System;
using System.Collections.Generic;
using System.Linq;
using CQRSharp.Generators;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    private static readonly EquatableArray<ClosedBehaviorModel> NoClosedBehaviors = new(Array.Empty<ClosedBehaviorModel>());
    private static readonly EquatableArray<ClosedBehaviorGapModel> NoClosedBehaviorGaps = new(Array.Empty<ClosedBehaviorGapModel>());

    // One registration per CQRSharp interface the type implements, in interface order. Validators, exception hooks and
    // closed pipeline behaviors are discovered services: registered under DiscoveredServices.Key and merged with the
    // application's own registrations where they are consumed, so one the application also registers runs once. Request
    // handlers get a plain forwarder, which lets a consumer resolve one by its interface. An interface closed over a type
    // generated code cannot name gets neither - it would not compile - and is reported instead (CQRGEN010). A
    // notification handler gets neither: it is delivered through its subscription, by concrete type, and a forwarder
    // would make it look like one the application registered by hand.
    private static (string[] Forwarders, string[] Discovered) BuildForwarders(
        INamedTypeSymbol type,
        Compilation compilation,
        CqrsKnownSymbols known,
        List<string> inaccessible)
    {
        var forwarders = new List<string>();
        var discovered = new List<string>();
        foreach (var iface in type.AllInterfaces)
        {
            if (!known.IsRegisteredRole(iface)) continue;

            var hidden = false;
            foreach (var argument in iface.TypeArguments)
                if (!GeneratedCodeAccessibility.IsAccessible(argument, compilation))
                {
                    inaccessible.Add(InaccessiblePartOf(argument, compilation).ToDisplayString(Fq));
                    hidden = true;
                }

            var definition = iface.OriginalDefinition;
            if (hidden || SymbolEqualityComparer.Default.Equals(definition, known.INotificationHandler)) continue;

            var isDiscoveredService = SymbolEqualityComparer.Default.Equals(definition, known.IRequestValidator1) ||
                                      SymbolEqualityComparer.Default.Equals(definition, known.IRequestExceptionAction2) ||
                                      SymbolEqualityComparer.Default.Equals(definition, known.IRequestExceptionHandler3) ||
                                      SymbolEqualityComparer.Default.Equals(definition, known.IPipelineBehavior) ||
                                      SymbolEqualityComparer.Default.Equals(definition, known.IStreamPipelineBehavior) ||
                                      SymbolEqualityComparer.Default.Equals(definition, known.INotificationPipelineBehavior);
            (isDiscoveredService ? discovered : forwarders).Add(iface.ToDisplayString(FqNullable));
        }

        return (forwarders.ToArray(), discovered.ToArray());
    }

    // The payload fingerprints of the idempotent requests this handler serves that no other module fingerprints: a
    // closed generic request (its declaring assembly only fingerprints the non-generic requests it declares), or one
    // declared in an assembly the generator does not run in (a contracts project). A request declared in this
    // compilation, or in another assembly with a module of its own, is fingerprinted where it is declared, so exactly
    // one module owns each type's fingerprint.
    private static FingerprintModel[] BuildHandledRequestFingerprints(INamedTypeSymbol type, Compilation compilation, CqrsKnownSymbols known)
    {
        List<FingerprintModel>? fingerprints = null;
        foreach (var iface in type.AllInterfaces)
        {
            if (!known.IsDispatchHandler(iface) || iface.TypeArguments.Length == 0) continue;
            if (iface.TypeArguments[0] is not INamedTypeSymbol request || !GeneratedCodeAccessibility.IsAccessible(request, compilation)) continue;
            if (IsFingerprintedWhereDeclared(request, compilation, known)) continue;

            if (BuildFingerprint(request, compilation, known) is { } fingerprint)
                (fingerprints ??= new List<FingerprintModel>()).Add(fingerprint);
        }

        return fingerprints?.ToArray() ?? Array.Empty<FingerprintModel>();
    }

    private static bool IsFingerprintedWhereDeclared(INamedTypeSymbol request, Compilation compilation, CqrsKnownSymbols known)
    {
        if (request.IsGenericType) return false;
        if (SymbolEqualityComparer.Default.Equals(request.ContainingAssembly, compilation.Assembly)) return true;
        return HasGeneratedModule(request.ContainingAssembly, known);
    }

    private static bool HasGeneratedModule(IAssemblySymbol assembly, CqrsKnownSymbols known)
    {
        var moduleMarker = known.CqrsGeneratedModuleAttribute;
        return moduleMarker is not null && assembly.GetAttributes()
            .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, moduleMarker));
    }

    // One binding per request-handler interface, dispatched the way its request is (the request's own shape, never the
    // handler's reading of it). A handler declared over a wider response than its request is dispatched with - legal
    // through IQuery<out T>/IStreamRequest<out T> covariance - is never called by the dispatcher, so it is not bound and is
    // reported instead (CQRGEN017). Bindings over types generated code cannot name were reported by BuildForwarders.
    private static HandlerImplModel[] BuildHandlerImpls(
        INamedTypeSymbol type,
        Compilation compilation,
        CqrsKnownSymbols known,
        List<ResultMismatchModel> mismatches)
    {
        var implName = type.ToDisplayString(Fq);
        var result = new List<HandlerImplModel>();

        foreach (var iface in type.AllInterfaces)
        {
            if (!known.IsDispatchHandler(iface) || SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, known.INotificationHandler)) continue;
            if (!iface.TypeArguments.All(a => GeneratedCodeAccessibility.IsAccessible(a, compilation))) continue;

            var requestType = iface.TypeArguments[0];
            if (known.ShapeOf(requestType) is not { } shape) continue;

            var declared = DeclaredResponseOf(iface, known);
            if (declared is null || !SymbolEqualityComparer.Default.Equals(declared, shape.Response))
            {
                mismatches.Add(new ResultMismatchModel(
                    iface.ToDisplayString(),
                    requestType.ToDisplayString(),
                    declared?.ToDisplayString() ?? "?",
                    shape.Response.ToDisplayString()));
                continue;
            }

            result.Add(new HandlerImplModel(
                implName,
                iface.ToDisplayString(FqNullable),
                iface.ToDisplayString(Fq),
                BuildRequestModel(requestType, shape, compilation, known),
                BuildRequestMetadata(requestType, compilation, known)));
        }

        return result.ToArray();
    }

    // The response a request-handler interface is declared over: CommandResult for a command handler, CommandResult<T>
    // for a value-returning one, the query result, IAsyncEnumerable<TItem> for a stream handler.
    private static ITypeSymbol? DeclaredResponseOf(INamedTypeSymbol handlerInterface, CqrsKnownSymbols known)
    {
        var definition = handlerInterface.OriginalDefinition;
        if (SymbolEqualityComparer.Default.Equals(definition, known.ICommandHandler))
            return known.CommandResult;
        if (SymbolEqualityComparer.Default.Equals(definition, known.IResultCommandHandler))
            return known.CommandResultGeneric?.Construct(handlerInterface.TypeArguments[1]);
        if (SymbolEqualityComparer.Default.Equals(definition, known.IQueryHandler))
            return handlerInterface.TypeArguments[1];
        if (SymbolEqualityComparer.Default.Equals(definition, known.IStreamRequestHandler))
            return known.AsyncEnumerable?.Construct(handlerInterface.TypeArguments[1]);
        return null;
    }

    // The request as it is dispatched, with its behaviors closed at compile time when its result or streamed item is a
    // value type (an IQuery<int>, an IStreamRequest<Guid>): the container cannot close an open-generic behavior over
    // one without dynamic code.
    private static RequestModel BuildRequestModel(ITypeSymbol requestType, CqrsRequestShape shape, Compilation compilation, CqrsKnownSymbols known)
    {
        var closed = NoClosedBehaviors;
        var gaps = NoClosedBehaviorGaps;
        if (requestType is INamedTypeSymbol request && shape.ResultOrItem is { IsValueType: true } resultOrItem)
        {
            var behaviors = BuildClosedBehaviors(request, new[] { requestType, resultOrItem }, KindOf(shape), compilation, known);
            closed = new EquatableArray<ClosedBehaviorModel>(behaviors.Closed);
            gaps = new EquatableArray<ClosedBehaviorGapModel>(behaviors.Gaps);
        }

        return new RequestModel(
            shape.Kind,
            requestType.ToDisplayString(Fq),
            requestType.ToDisplayString(FqNullable),
            shape.Response.ToDisplayString(Fq),
            shape.ResultOrItem?.ToDisplayString(FqNullable),
            closed,
            gaps);
    }

    private static BehaviorKind KindOf(CqrsRequestShape shape)
        => shape.Kind == CqrsRequestKind.Stream ? BehaviorKind.Stream : BehaviorKind.Pipeline;
}
