using CQRSharp.Core.Notifications;
using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     The <c>CQRCONF</c> checks: misconfigurations that would otherwise degrade silently at runtime (an idle outbox
///     processor, a transactional outbox with no transaction to join, notifications that bypass the outbox, markers whose
///     behavior is not wired). Asks the container whether services are registered rather than activating them where the
///     answer allows it, and performs no reflection over consumer types.
/// </summary>
internal static class CqrsConfigurationInspector
{
    /// <summary>
    ///     The configuration issues of the given scope, outbox options and request descriptions, each with a stable code
    ///     and a self-contained message; empty when nothing is wrong.
    /// </summary>
    public static IReadOnlyList<CqrsBindingIssue> Inspect(
        IServiceProvider services,
        OutboxOptions outbox,
        IReadOnlyList<CqrsRequestBinding> requestBindings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(requestBindings);

        var issues = new List<CqrsBindingIssue>();
        var isService = services.GetService<IServiceProviderIsService>();
        var outboxEnabled = outbox.Mode != OutboxMode.Disabled;

        if (outboxEnabled) InspectOutbox(services, isService, outbox, issues);

        foreach (var binding in requestBindings)
            InspectMarkers(binding, issues);

        return issues;
    }

    private static void InspectOutbox(IServiceProvider services, IServiceProviderIsService? isService, OutboxOptions outbox, List<CqrsBindingIssue> issues)
    {
        // CQRCONF001: an enabled outbox mode needs a store, a serializer and the subscriptions, or the processor sits idle
        // and nothing published through the outbox is ever delivered.
        var missing = new List<string>();
        if (!IsRegistered(services, isService, typeof(IOutboxStore))) missing.Add(nameof(IOutboxStore));
        if (!IsRegistered(services, isService, typeof(INotificationSerializer))) missing.Add(nameof(INotificationSerializer));
        if (!IsRegistered(services, isService, typeof(INotificationSubscriptionRegistry))) missing.Add(nameof(INotificationSubscriptionRegistry));

        if (missing.Count > 0)
            issues.Add(new CqrsBindingIssue(
                CqrsBindingIssueSeverity.Error,
                "CQRCONF001",
                $"Outbox mode is '{outbox.Mode}' but the following required outbox service(s) are not registered: " +
                $"{string.Join(", ", missing)}. The outbox processor will never deliver notifications. Enable the " +
                "outbox through the builder (UseOutbox(o => o.UseInMemoryStore()) for development, or a durable store " +
                "such as o.UseRedis(...) / o.UseEntityFrameworkCore<TContext>()), or leave it off (outbox mode Disabled)."));

        // CQRCONF007: Transactional mode stores a notification only while the scope's IUnitOfWork has an active transaction;
        // with no IUnitOfWork registered there never is one, so the configured durability is silently absent.
        if (outbox.Mode == OutboxMode.Transactional && !IsRegistered(services, isService, typeof(IUnitOfWork)))
            issues.Add(CqrsConfigurationRules.TransactionalOutboxWithoutUnitOfWork);

        // CQRCONF009: an outbox message is addressed to a handler by its stable name, so two handler types sharing one
        // name (two assemblies with the same namespace and type name, or two [NotificationHandlerName]s with the same
        // value) would make every message for that name ambiguous.
        var subscriptions = services.GetService<INotificationSubscriptionRegistry>();
        if (subscriptions is not null)
            foreach (var group in subscriptions.Subscriptions
                         .GroupBy(s => s.HandlerName, StringComparer.Ordinal)
                         .Where(g => g.Select(s => s.HandlerType).Distinct().Count() > 1)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var handlerTypes = string.Join(", ", group.Select(s => s.HandlerType.FullName).Distinct().OrderBy(n => n, StringComparer.Ordinal));
                issues.Add(new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Error,
                    "CQRCONF009",
                    $"The notification handler name '{group.Key}' is used by more than one handler type ({handlerTypes}), so an " +
                    "outbox message addressed to it is ambiguous. Give each handler a unique [NotificationHandlerName]."));
            }

        var serializer = services.GetService<INotificationSerializer>();

        // CQRCONF010: two modules give different notification types one stable name, so a stored message could not be
        // read back as the type it was stored as.
        if (serializer is not null)
            issues.AddRange(CqrsConfigurationRules.NotificationNameConflicts(serializer));

        // CQRCONF003: a notification with handlers that the registered serializer gives no name cannot be stored, so it
        // is dispatched in-process although an outbox mode is enabled: a silent bypass of the outbox. The question goes to
        // the serializer the dispatcher asks, so the answer holds for a custom one too. Without any serializer CQRCONF001
        // already names the cause. A type that lost its name to another module's is the CQRCONF010 above.
        var publisher = services.GetService<NotificationPublisher>();
        if (serializer is not null && subscriptions is not null && publisher is not null)
            foreach (var notificationType in publisher.RoutedTypes.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (subscriptions.GetSubscriptions(notificationType).Count == 0) continue;
                if (CqrsConfigurationRules.UnnamedHandledNotification(notificationType, serializer, outbox.Mode) is { Code: "CQRCONF003" } unnamed)
                    issues.Add(unnamed);
            }

        // CQRCONF011: the outbox stores one message per subscription, and a handler registered by hand has none, so a
        // durable notification that goes through the outbox never reaches the handlers registered by hand for it.
        if (serializer is not null && publisher is not null)
            foreach (var notificationType in publisher.RoutedTypes.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (!serializer.TryGetNotificationName(notificationType, out var name)) continue;

                // Telling a hand-registered handler from a subscribed one means constructing it: a handler that cannot be
                // constructed is reported, as the request describer reports a behavior that cannot be, instead of
                // aborting the whole inspection (and the host start that runs it).
                Type[] byHand;
                try
                {
                    byHand = publisher.HandRegisteredHandlerTypes(notificationType, services);
                }
                catch (Exception ex)
                {
                    issues.Add(CqrsConfigurationRules.UnresolvableHandRegisteredHandlers(notificationType, ex));
                    continue;
                }

                if (byHand.Length > 0)
                    issues.Add(CqrsConfigurationRules.HandRegisteredHandlersOfDurableNotification(notificationType, name, byHand));
            }
    }

    // CQRCONF005 / CQRCONF006: a request opts into idempotency or retries through a marker interface, but the behavior
    // that honors it is not in the request's pipeline, so the marker has no effect.
    private static void InspectMarkers(CqrsRequestBinding binding, List<CqrsBindingIssue> issues)
    {
        if (!CqrsConfigurationRules.HasBehaviorMarkers(binding.RequestType)) return;

        // When the behaviors could not be resolved (CQRDIAG004, reported with the binding) the pipeline is unknown, not
        // empty: concluding that a behavior is missing would name the wrong cause.
        if (binding.Issues.Any(i => i.Code == "CQRDIAG004")) return;

        var wired = binding.Pipeline.Concat(binding.ExemptedPipeline).Select(b => b.BehaviorType).ToArray();
        issues.AddRange(CqrsConfigurationRules.MissingMarkerBehaviors(binding.RequestType, wired));
    }

    private static bool IsRegistered(IServiceProvider services, IServiceProviderIsService? isService, Type serviceType)
        => isService?.IsService(serviceType) ?? services.GetService(serviceType) is not null;
}
