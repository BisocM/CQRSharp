using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    private static void GenerateRequestRegistry(
        StringBuilder sb,
        ImmutableArray<CandidateModel> candidates,
        KnownSnapshot known,
        Dictionary<string, List<HandlerImplModel>> handlerBindingsByRequest,
        SourceProductionContext context,
        GeneratorConfig config)
    {
        sb.AppendLine();
        sb.AppendLine("            // Registering Request Registry");
        sb.AppendLine("            var requestMetadataMappings = new ConcurrentDictionary<Type, global::CQRSharp.RequestMetadata>();");

        if (string.IsNullOrEmpty(known.PreHandlerInterfaceName) ||
            string.IsNullOrEmpty(known.PostHandlerInterfaceName) ||
            string.IsNullOrEmpty(known.PipelineExemptionAttributeName))
            return;

        // Diagnostics: request types declared in this compilation should have exactly one handler.
        if (!config.SuppressMissingRequestHandlerDiagnostics)
        {
            foreach (var candidate in candidates.Where(c => c.Request is not null))
            {
                var requestKey = candidate.Request!.RequestTypeName;
                if (handlerBindingsByRequest.ContainsKey(requestKey)) continue;

                var location = candidate.Location?.ToLocation() ?? Location.None;
                context.ReportDiagnostic(Diagnostic.Create(MissingRequestHandlerDiagnostic, location, requestKey));
            }
        }

        var locationByName = new Dictionary<string, LocationInfo?>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
            if (!locationByName.ContainsKey(candidate.TypeName))
                locationByName[candidate.TypeName] = candidate.Location;

        foreach (var kvp in handlerBindingsByRequest.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var bindings = kvp.Value;
            if (bindings.Count == 0) continue;

            var selected = SelectDeterministicBinding(bindings);

            if (bindings.Count > 1)
            {
                var descriptions = string.Join(
                    ", ",
                    bindings
                        .OrderBy(b => b.ImplTypeName, StringComparer.Ordinal)
                        .ThenBy(b => b.InterfaceNameOrdinal, StringComparer.Ordinal)
                        .Select(b => $"{b.ImplTypeName} ({b.InterfaceNameOrdinal})"));

                var location = (locationByName.TryGetValue(selected.ImplTypeName, out var loc) ? loc?.ToLocation() : null) ?? Location.None;
                context.ReportDiagnostic(Diagnostic.Create(MultipleRequestHandlersDiagnostic, location, kvp.Key, descriptions));
            }

            var requestTypeName = selected.RequestTypeName;
            var metadata = selected.RequestMetadata;

            var preHandlersCode = RenderAttributeArray(metadata.PreHandlers, known.PreHandlerInterfaceName);
            var postHandlersCode = RenderAttributeArray(metadata.PostHandlers, known.PostHandlerInterfaceName);
            var pipelineExemptionsCode = RenderAttributeArray(metadata.PipelineExemptions, known.PipelineExemptionAttributeName);

            var resultTypeCode = selected.ResultTypeName is not null
                ? $"typeof({selected.ResultTypeName})"
                : "null";

            sb.AppendLine(
                $"            requestMetadataMappings.TryAdd(typeof({requestTypeName}), new global::CQRSharp.RequestMetadata(typeof({requestTypeName}), typeof({selected.ImplTypeName}), {preHandlersCode}, {postHandlersCode}, {pipelineExemptionsCode}, {resultTypeCode}, typeof({metadata.ContextTypeName})));");
        }
    }

    private static string RenderAttributeArray(EquatableArray<AttributeModel> attributes, string fullyQualifiedInterfaceName)
    {
        if (attributes.Count == 0) return $"System.Array.Empty<{fullyQualifiedInterfaceName}>()";

        var instancesCode = attributes.Select(attr =>
            $"new {attr.AttributeTypeName}({string.Join(", ", attr.ConstructorArgs)})" +
            (attr.NamedArgs.Count == 0 ? string.Empty : $" {{ {string.Join(", ", attr.NamedArgs)} }}"));

        return $"new {fullyQualifiedInterfaceName}[] {{ {string.Join(", ", instancesCode)} }}";
    }

    // One typed invoker per request: a static lambda that returns the handler's own Task<TResult> (commands, queries) or
    // IAsyncEnumerable<TItem> (streams), so a dispatch neither boxes the result nor pays for an async wrapper. The executor
    // casts it back to Func<object, TRequest, CancellationToken, ...> with the same result type the generated dispatcher
    // closes the executor's entry point over.
    private static void GenerateHandlerRegistry(
        StringBuilder sb,
        Dictionary<string, List<HandlerImplModel>> handlerBindingsByRequest,
        KnownSnapshot known)
    {
        sb.AppendLine();
        sb.AppendLine("            // Registering Handler Registry");
        sb.AppendLine("            var handlerInvokerMappings = new Dictionary<Type, Delegate>();");
        // The delegate is closed over the un-annotated result type (nullability is erased at runtime, and that is the
        // type the executor casts to); a handler declared over 'User?' would otherwise warn on the conversion.
        sb.AppendLine("#pragma warning disable CS8619, CS8620");

        foreach (var kvp in handlerBindingsByRequest.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var bindings = kvp.Value;
            if (bindings.Count == 0) continue;
            var selected = SelectDeterministicBinding(bindings);

            // Streams record their result as IAsyncEnumerable<TItem>; the handler returns it directly (no Task).
            var resultType = selected.Kind == HandlerKind.Command ? known.CommandResultTypeName : selected.ResultTypeName;
            if (string.IsNullOrEmpty(resultType)) continue;

            var returnType = selected.Kind == HandlerKind.Stream
                ? resultType
                : $"global::System.Threading.Tasks.Task<{resultType}>";

            var requestType = selected.RequestTypeName;
            sb.AppendLine(
                $"            handlerInvokerMappings[typeof({requestType})] = new Func<object, {requestType}, global::System.Threading.CancellationToken, {returnType}>(" +
                $"static (handler, request, ct) => (({selected.InterfaceNameNullable})handler).Handle(request, ct));");
        }

        sb.AppendLine("#pragma warning restore CS8619, CS8620");
    }

    private static void GenerateContextFactoryRegistry(StringBuilder sb, ImmutableArray<CandidateModel> candidates, KnownSnapshot known)
    {
        sb.AppendLine();
        sb.AppendLine("            // Registering Context Factory Registry");
        sb.AppendLine("            var factoryMappings = new ConcurrentDictionary<Type, Func<System.IServiceProvider, object?>>();");

        if (string.IsNullOrEmpty(known.RequestContextBaseTypeName)) return;

        var contextTypes = candidates
            .SelectMany(c => c.ContextFactories)
            .Distinct(StringComparer.Ordinal);

        foreach (var contextTypeName in contextTypes)
            sb.AppendLine($"            factoryMappings.TryAdd(typeof({contextTypeName}), sp => sp.GetService<IRequestContextFactory<{contextTypeName}>>());");

        // Always register the default RequestContextBase factory (provided by CQRSharp.Core).
        sb.AppendLine(
            $"            factoryMappings.TryAdd(typeof({known.RequestContextBaseTypeName}), sp => sp.GetService<global::CQRSharp.IRequestContextFactory>());");
    }

    private static void GenerateRequestExceptionHookRegistry(StringBuilder sb, ImmutableArray<CandidateModel> candidates)
    {
        sb.AppendLine();
        sb.AppendLine("            // Registering Request Exception Hook Registry");
        sb.AppendLine(
            "            var exceptionHookMappings = new ConcurrentDictionary<Type, global::CQRSharp.Core.Exceptions.RequestExceptionHookInvoker>();");

        var hooks = candidates.SelectMany(c => c.ExceptionHooks).ToArray();

        foreach (var requestGroup in hooks
                     .GroupBy(h => h.RequestTypeName, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var requestTypeName = requestGroup.Key;
            var resultTypeName = requestGroup.First().ResultTypeName;

            sb.AppendLine($"            exceptionHookMappings.TryAdd(typeof({requestTypeName}), async (sp, request, exception, ct) =>");
            sb.AppendLine("            {");
            sb.AppendLine($"                var typedRequest = ({requestTypeName})request;");

            var byException = requestGroup
                .GroupBy(h => h.ExceptionTypeName, StringComparer.Ordinal)
                .Select(g => new
                {
                    ExceptionTypeName = g.Key,
                    Depth = g.First().ExceptionInheritanceDepth,
                    Kind = g.Aggregate(ExceptionHookKind.None, (acc, h) => acc | h.Kind)
                })
                .OrderByDescending(e => e.Depth)
                .ThenBy(e => e.ExceptionTypeName, StringComparer.Ordinal);

            foreach (var exception in byException)
            {
                var exceptionTypeName = exception.ExceptionTypeName;
                var kind = exception.Kind;

                sb.AppendLine("                {");
                sb.AppendLine($"                    if (exception is {exceptionTypeName} typedException)");
                sb.AppendLine("                    {");

                if ((kind & ExceptionHookKind.Action) != 0)
                {
                    sb.AppendLine(
                        $"                        var actions = sp.GetServices<global::CQRSharp.IRequestExceptionAction<{requestTypeName}, {exceptionTypeName}>>();");
                    sb.AppendLine("                        foreach (var action in actions)");
                    sb.AppendLine("                            await action.Execute(typedRequest, typedException, ct).ConfigureAwait(false);");
                }

                if ((kind & ExceptionHookKind.Handler) != 0)
                {
                    sb.AppendLine(
                        $"                        var handlers = sp.GetServices<global::CQRSharp.IRequestExceptionHandler<{requestTypeName}, {resultTypeName}, {exceptionTypeName}>>();");
                    sb.AppendLine(
                        $"                        var state = new global::CQRSharp.RequestExceptionHandlerState<{resultTypeName}>();");
                    sb.AppendLine("                        foreach (var handler in handlers)");
                    sb.AppendLine("                        {");
                    sb.AppendLine("                            await handler.Handle(typedRequest, typedException, state, ct).ConfigureAwait(false);");
                    sb.AppendLine("                            if (!state.Handled) continue;");
                    sb.AppendLine(
                        "                            return global::CQRSharp.Core.Exceptions.RequestExceptionHandlingOutcome.HandledWith(state.Response);");
                    sb.AppendLine("                        }");
                }

                sb.AppendLine("                    }");
                sb.AppendLine("                }");
            }

            sb.AppendLine("                return global::CQRSharp.Core.Exceptions.RequestExceptionHandlingOutcome.NotHandled;");
            sb.AppendLine("            });");
        }
    }
}
