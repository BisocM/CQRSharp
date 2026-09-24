using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    // The tables of the generated module. Each is a static field initialized once, keyed by exact type, and every value
    // is data, a static lambda closed over concrete types, or a CQRSharp.Core object created by closing one of Core's
    // generic factories over this assembly's types - AOT-safe, and with the behavior behind it in Core. Every name is
    // fully qualified: generated code must not bind to a consumer type that happens to share a framework name.

    private const string TypeName = "global::System.Type";
    private const string RequestMetadataName = "global::CQRSharp.Core.Registries.RequestMetadata";

    // The requests this module routes: those it declares, plus those it handles without declaring - a closed generic
    // request (GetById<int>), or one declared in an assembly the generator does not run in (a contracts-only project).
    // Both read the request's own shape, so a request is routed the same way whichever side it came from. Every
    // per-request table (routes, and through them diagnostics) is built from this one list, so a request that
    // dispatches is also described.
    private static List<RequestModel> GetRoutableRequests(ImmutableArray<CandidateModel> candidates)
        => candidates
            .Where(c => c.Request is not null)
            .Select(c => c.Request!)
            .Concat(candidates.SelectMany(c => c.Handlers).Select(h => h.Request))
            .GroupBy(r => r.RequestTypeName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(r => r.RequestTypeName, StringComparer.Ordinal)
            .ToList();

    private static void EmitTypeMap(StringBuilder sb, string field, string valueType, IEnumerable<(string Key, string Value)> entries)
    {
        var mapType = $"global::System.Collections.Generic.Dictionary<{TypeName}, {valueType}>";
        sb.AppendLine($"        private static readonly {mapType} {field} = new {mapType}");
        sb.AppendLine("        {");
        foreach (var (key, value) in entries)
            sb.AppendLine($"            [typeof({key})] = {value},");
        sb.AppendLine("        };");
        sb.AppendLine();
    }

    private static void EmitRequestMetadata(StringBuilder sb, Dictionary<string, List<HandlerImplModel>> handlerBindingsByRequest)
    {
        var entries = new List<(string, string)>();
        foreach (var bindings in handlerBindingsByRequest.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
        {
            var selected = SelectDeterministicBinding(bindings);
            var metadata = selected.RequestMetadata;
            entries.Add((selected.Request.RequestTypeName,
                $"new {RequestMetadataName}(typeof({selected.ImplTypeName}), typeof({metadata.ContextTypeName}), " +
                $"{RenderAttributeArray(metadata.PreHandlers, "global::CQRSharp.IPreHandlerAttribute")}, " +
                $"{RenderAttributeArray(metadata.PostHandlers, "global::CQRSharp.IPostHandlerAttribute")}, " +
                $"{RenderAttributeArray(metadata.PipelineExemptions, "global::CQRSharp.PipelineExemptionAttribute")})"));
        }

        EmitTypeMap(sb, "__requestMetadata", RequestMetadataName, entries);
    }

    private static string RenderAttributeArray(EquatableArray<AttributeModel> attributes, string fullyQualifiedInterfaceName)
    {
        if (attributes.Count == 0) return $"global::System.Array.Empty<{fullyQualifiedInterfaceName}>()";

        var instancesCode = attributes.Select(attr =>
            $"new {attr.AttributeTypeName}({string.Join(", ", attr.ConstructorArgs)})" +
            (attr.NamedArgs.Count == 0 ? string.Empty : $" {{ {string.Join(", ", attr.NamedArgs)} }}"));

        return $"new {fullyQualifiedInterfaceName}[] {{ {string.Join(", ", instancesCode)} }}";
    }

    // One typed invoker per request: a static lambda that returns the handler's own Task<TResult> (commands, queries) or
    // IAsyncEnumerable<TItem> (streams), so a dispatch neither boxes the result nor pays for an async wrapper. The
    // executor casts it back to the Func type its route closes the executor's entry point over.
    private static void EmitHandlerInvokers(StringBuilder sb, Dictionary<string, List<HandlerImplModel>> handlerBindingsByRequest)
    {
        var entries = new List<(string, string)>();
        foreach (var bindings in handlerBindingsByRequest.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
        {
            var selected = SelectDeterministicBinding(bindings);
            var request = selected.Request;

            // A stream handler returns its IAsyncEnumerable<TItem> directly (no Task).
            var returnType = request.Kind == CqrsRequestKind.Stream
                ? request.ResponseTypeName
                : $"global::System.Threading.Tasks.Task<{request.ResponseTypeName}>";

            entries.Add((request.RequestTypeName,
                $"new global::System.Func<object, {request.RequestTypeName}, global::System.Threading.CancellationToken, {returnType}>(" +
                $"static (handler, request, ct) => (({selected.InterfaceNameNullable})handler).Handle(request, ct))"));
        }

        // The delegate is closed over the un-annotated result type (nullability is erased at runtime, and that is the type
        // the executor casts to); a handler declared over 'User?' would otherwise warn on the conversion.
        sb.AppendLine("#pragma warning disable CS8619, CS8620");
        EmitTypeMap(sb, "__handlerInvokers", "global::System.Delegate", entries);
        sb.AppendLine("#pragma warning restore CS8619, CS8620");
        sb.AppendLine();
    }

    // Every context type this module's requests are created with, and every one its factories create: a context type is
    // then resolvable wherever its factory lives or is registered, and every module maps a type to the same source.
    private static void EmitContextSources(StringBuilder sb, ImmutableArray<CandidateModel> candidates, Dictionary<string, List<HandlerImplModel>> handlerBindingsByRequest)
    {
        var contextTypes = handlerBindingsByRequest.Values
            .Select(bindings => SelectDeterministicBinding(bindings).RequestMetadata.ContextTypeName)
            .Concat(candidates.SelectMany(c => c.ContextFactories))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal);

        EmitTypeMap(sb, "__contextSources", "global::CQRSharp.Core.Registries.RequestContextSource",
            contextTypes.Select(t => (t, $"global::CQRSharp.Core.Registries.RequestContextSource.For<{t}>()")));
    }

    private static void EmitRoutes(StringBuilder sb, List<RequestModel> routable)
    {
        EmitTypeMap(sb, "__requestRoutes", "global::CQRSharp.Core.Pipelines.RequestRoute", routable
            .Where(r => r.Kind != CqrsRequestKind.Stream)
            .Select(r => (r.RequestTypeName, r.Kind == CqrsRequestKind.Command
                ? $"global::CQRSharp.Core.Pipelines.RequestRoute.Command<{r.RequestTypeNameNullable}>()"
                : $"global::CQRSharp.Core.Pipelines.RequestRoute.Query<{r.RequestTypeNameNullable}, {r.ResultOrItemNullable}>()")));

        EmitTypeMap(sb, "__streamRoutes", "global::CQRSharp.Core.Pipelines.StreamRoute", routable
            .Where(r => r.Kind == CqrsRequestKind.Stream)
            .Select(r => (r.RequestTypeName, $"global::CQRSharp.Core.Pipelines.StreamRoute.For<{r.RequestTypeNameNullable}, {r.ResultOrItemNullable}>()")));
    }

    // One entry per (request, exception type) pair, closing Core's hook factory over the pair and the response the
    // request is dispatched with: an actions invoker when this module declares an action for the pair, a handlers invoker
    // when it declares a handler. The composition merges a pair across modules role by role, so every module's hooks for
    // it run, each once.
    private static void EmitExceptionHooks(StringBuilder sb, ImmutableArray<CandidateModel> candidates)
    {
        sb.AppendLine("        private static readonly global::CQRSharp.Core.Exceptions.RequestExceptionHook[] __exceptionHooks =");
        sb.AppendLine("        {");

        var pairs = candidates.SelectMany(c => c.ExceptionHooks)
            .GroupBy(h => (h.RequestTypeName, h.ExceptionTypeName))
            .OrderBy(g => g.Key.RequestTypeName, StringComparer.Ordinal)
            .ThenBy(g => g.Key.ExceptionTypeName, StringComparer.Ordinal);

        foreach (var pair in pairs)
        {
            var (requestTypeName, exceptionTypeName) = pair.Key;
            var kind = pair.Aggregate(ExceptionHookKind.None, (acc, h) => acc | h.Kind);
            var actions = (kind & ExceptionHookKind.Action) != 0 ? "true" : "false";
            var handlers = (kind & ExceptionHookKind.Handler) != 0 ? "true" : "false";
            sb.AppendLine(
                $"            global::CQRSharp.Core.Exceptions.RequestExceptionHook.For<{requestTypeName}, {pair.First().ResponseTypeName}, {exceptionTypeName}>" +
                $"(actions: {actions}, handlers: {handlers}),");
        }

        sb.AppendLine("        };");
        sb.AppendLine();
    }

    // A route for every concrete notification type this module declares or handles: what lets the runtime publish a
    // notification held as a base type or INotification as its own type, behaviors included.
    private static void EmitNotificationRoutes(StringBuilder sb, ImmutableArray<CandidateModel> candidates)
    {
        var notificationTypes = candidates
            .Where(c => c.Notification is not null)
            .Select(c => c.Notification!.TypeName)
            .Concat(candidates.SelectMany(c => c.HandledConcreteNotifications))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal);

        EmitTypeMap(sb, "__notificationRoutes", "global::CQRSharp.Core.Notifications.NotificationRoute",
            notificationTypes.Select(t => (t, $"global::CQRSharp.Core.Notifications.NotificationRoute.For<{t}>()")));
    }

    // One subscription per (handler, handled notification type), ordered by handler name so a notification's handlers run,
    // and its messages are stored, in a stable order. A blank [NotificationHandlerName] already fell back to the default.
    private static void EmitNotificationSubscriptions(StringBuilder sb, ImmutableArray<CandidateModel> candidates)
    {
        var subscriptions = candidates
            .Where(c => c.HandledNotifications.Count > 0 && c.NotificationHandlerName is not null)
            .SelectMany(c => c.HandledNotifications.Select(n => (Notification: n, Handler: c.TypeName, Name: c.NotificationHandlerName!)))
            .Distinct()
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Notification, StringComparer.Ordinal);

        sb.AppendLine("        private static readonly global::CQRSharp.Core.Notifications.NotificationSubscription[] __notificationSubscriptions =");
        sb.AppendLine("        {");
        foreach (var subscription in subscriptions)
            sb.AppendLine(
                $"            global::CQRSharp.Core.Notifications.NotificationSubscription.For<{subscription.Handler}, {subscription.Notification}>(" +
                $"{SymbolDisplay.FormatLiteral(subscription.Name, true)}),");
        sb.AppendLine("        };");
        sb.AppendLine();
    }

    // [NotificationName(PartitionBy = nameof(X))]: a typed selector per notification, formatted by the runtime helper so
    // a Guid, number or date key renders the same on every machine.
    private static void EmitPartitionKeySelectors(StringBuilder sb, IReadOnlyList<NotificationModel> stableNotifications)
    {
        EmitTypeMap(sb, "__partitionKeySelectors", "global::System.Func<global::CQRSharp.INotification, string?>", stableNotifications
            .Where(n => n.PartitionBy is not null && n.PartitionByResolved)
            .Select(n => (n.TypeName,
                $"static n => global::CQRSharp.Core.Outbox.OutboxPartitionKey.From((({n.TypeName})n).{EscapeIdentifier(n.PartitionBy!)})")));
    }
}
