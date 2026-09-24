using CQRSharp.Core.Modules;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
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

        if (outbox.Mode == OutboxMode.Transactional)
        {
            // CQRCONF007: Transactional mode stores a notification only while the scope's IUnitOfWork has an active
            // transaction; with no IUnitOfWork registered there never is one, so the configured durability is silently
            // absent.
            if (!IsRegistered(services, isService, typeof(IUnitOfWork)))
                issues.Add(new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Error,
                    "CQRCONF007",
                    "Outbox mode is 'Transactional' but no IUnitOfWork is registered, so there is never an active " +
                    "transaction and no notification ever reaches the outbox: every one is dispatched in-process. Register a " +
                    "unit of work (the builder's UseUnitOfWork(...), or UseEntityFrameworkCoreUnitOfWork<TContext>() for EF " +
                    "Core), or use the Enabled mode (UseOutbox's default), which stores every named notification without one."));
        }

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
        if (serializer is CompositeOutboxNotificationSerializer generated)
            foreach (var conflict in generated.Conflicts)
                issues.Add(new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Error,
                    "CQRCONF010",
                    $"The notification name '{conflict.Name}' is given to more than one notification type " +
                    $"({string.Join(", ", conflict.Types.Select(t => t.FullName))}), so an outbox message stored under it " +
                    "cannot be read back as the type it was stored as. Give each type a unique [NotificationName]."));

        // CQRCONF003: a notification with handlers that the registered serializer gives no name cannot be stored, so it
        // is dispatched in-process although an outbox mode is enabled: a silent bypass of the outbox. The question goes to
        // the serializer the dispatcher asks, so the answer holds for a custom one too. Without any serializer CQRCONF001
        // already names the cause.
        var publisher = services.GetService<NotificationPublisher>();
        if (serializer is not null && subscriptions is not null && publisher is not null)
        {
            // The generated serializer names exactly the [NotificationName] types; a custom one names what it chooses.
            var remedy = serializer is CompositeOutboxNotificationSerializer
                ? "Add [NotificationName] to route it through the outbox."
                : $"The registered serializer '{serializer.GetType().FullName}' replaces the generated one, so [NotificationName] " +
                  "alone does not help: have its TryGetNotificationName name the type to route it through the outbox.";

            foreach (var notificationType in publisher.RoutedTypes.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (subscriptions.GetSubscriptions(notificationType).Count == 0) continue;
                if (serializer.TryGetNotificationName(notificationType, out _)) continue;

                // Under the generated serializer, two kinds of notification are dispatched in-process by what they are,
                // not through an omission this could point at: a value type ([NotificationName] applies to classes only),
                // and CQRSharp's own lifecycle notifications, which an application handles but cannot annotate. A custom
                // serializer can name either, so it is still asked.
                if (serializer is CompositeOutboxNotificationSerializer &&
                    (notificationType.IsValueType || notificationType.Assembly == typeof(CommandCompletedNotification).Assembly))
                    continue;

                issues.Add(new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Warning,
                    "CQRCONF003",
                    $"Notification '{notificationType.FullName}' has handlers but no stable name while outbox mode is " +
                    $"'{outbox.Mode}': the registered notification serializer does not name it, so it cannot be stored in the " +
                    $"outbox and will be dispatched in-process instead. {remedy}"));
            }
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
                    issues.Add(new CqrsBindingIssue(
                        CqrsBindingIssueSeverity.Error,
                        "CQRCONF012",
                        $"The notification handlers registered by hand for '{notificationType.FullName}' could not be resolved: " +
                        $"{ex.Message} Publishing it in-process fails the same way."));
                    continue;
                }

                if (byHand.Length == 0) continue;

                issues.Add(new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Warning,
                    "CQRCONF011",
                    $"Notification '{notificationType.FullName}' is durable (stored as '{name}'), but the handler(s) registered " +
                    $"by hand for it ({string.Join(", ", byHand.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal))}) " +
                    "have no outbox subscription, so a publish that goes through the outbox never reaches them; they run only " +
                    "when it is dispatched in-process. Declare them in an assembly the source generator runs in, where they are " +
                    "discovered and subscribed."));
            }
    }

    // CQRCONF005 / CQRCONF006: a request opts into idempotency or retries through a marker interface, but the behavior
    // that honors it is not in the request's pipeline, so the marker has no effect.
    private static void InspectMarkers(CqrsRequestBinding binding, List<CqrsBindingIssue> issues)
    {
        // When the behaviors could not be resolved (CQRDIAG004, reported with the binding) the pipeline is unknown, not
        // empty: concluding that a behavior is missing would name the wrong cause.
        if (binding.Issues.Any(i => i.Code == "CQRDIAG004")) return;

        // An exempted behavior is registered and deliberately skipped for the request: an explicit opt-out, not a gap.
        var wired = binding.Pipeline.Concat(binding.ExemptedPipeline);

        // An error: the marker is the request's contract that a repeated delivery is not processed twice, and without the
        // behavior every duplicate is processed.
        if (typeof(IIdempotentRequest).IsAssignableFrom(binding.RequestType) &&
            !wired.Any(b => typeof(ICqrsIdempotencyBehaviorMarker).IsAssignableFrom(b.BehaviorType)))
            issues.Add(new CqrsBindingIssue(
                CqrsBindingIssueSeverity.Error,
                "CQRCONF005",
                $"Request '{binding.RequestType.FullName}' implements IIdempotentRequest, but no idempotency behavior " +
                "is registered, so the marker has no effect (duplicate requests are NOT rejected). Enable it with " +
                "UseIdempotency(...)."));

        // A warning: without the behavior a failure is not retried, but nothing runs twice.
        if (typeof(IRetryableRequest).IsAssignableFrom(binding.RequestType) &&
            !wired.Any(b => typeof(ICqrsResilienceBehaviorMarker).IsAssignableFrom(b.BehaviorType)))
            issues.Add(new CqrsBindingIssue(
                CqrsBindingIssueSeverity.Warning,
                "CQRCONF006",
                $"Request '{binding.RequestType.FullName}' implements IRetryableRequest, but no resilience behavior " +
                "is registered, so the marker has no effect (failures are NOT retried). Enable it with UseResilience(...)."));
    }

    private static bool IsRegistered(IServiceProvider services, IServiceProviderIsService? isService, Type serviceType)
        => isService?.IsService(serviceType) ?? services.GetService(serviceType) is not null;
}
