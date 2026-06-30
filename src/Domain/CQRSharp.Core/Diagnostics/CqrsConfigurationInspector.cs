using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Inspects the wired-up CQRSharp services and options for global misconfigurations that would otherwise
///     degrade silently at runtime (an idle outbox processor, a transactional outbox that can never participate
///     in a transaction, notifications that bypass the outbox, or a missing generated registry from a misordered
///     setup). Probes the container with <see cref="ServiceProviderServiceExtensions.GetService{T}" /> and compares
///     types directly; performs no reflection over consumer types, no generic-type construction, and no activation.
///     Public because the source-generated diagnostics class, emitted into the consumer assembly, calls
///     <see cref="Inspect" /> across the assembly boundary.
/// </summary>
public static class CqrsConfigurationInspector
{
    /// <summary>
    ///     Produces the global configuration issues for the given resolved services, outbox/dispatcher options,
    ///     request bindings, and notification registry. Each issue carries a stable <c>CQRCONF</c> code and a
    ///     self-contained message; the returned list is empty when nothing is wrong.
    /// </summary>
    public static IReadOnlyList<CqrsBindingIssue> Inspect(
        IServiceProvider services,
        OutboxOptions outbox,
        DispatcherOptions dispatcher,
        IReadOnlyList<CqrsRequestBinding> requestBindings,
        ICqrsNotificationRegistry? notifications)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(requestBindings);

        var issues = new List<CqrsBindingIssue>();

        var outboxEnabled = outbox.Mode != OutboxMode.Disabled;

        // CQRCONF001: an enabled outbox mode needs the full store/serializer/dispatcher triplet, or the
        // OutboxProcessor sits idle forever and notifications are never delivered through the outbox.
        if (outboxEnabled)
        {
            var missing = new List<string>();
            if (services.GetService<IOutboxStore>() is null) missing.Add(nameof(IOutboxStore));
            if (services.GetService<INotificationSerializer>() is null) missing.Add(nameof(INotificationSerializer));
            if (services.GetService<IDirectNotificationDispatcher>() is null) missing.Add(nameof(IDirectNotificationDispatcher));

            if (missing.Count > 0)
                issues.Add(new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Error,
                    "CQRCONF001",
                    $"Outbox mode is '{outbox.Mode}' but the following required outbox service(s) are not registered: " +
                    $"{string.Join(", ", missing)}. The outbox processor will never deliver notifications. Enable the " +
                    "outbox through the builder — UseOutbox(o => o.UseInMemoryStore()) for development, or a durable store " +
                    "such as o.UseRedis(...) / o.UseEntityFrameworkCore<TContext>() — or leave it off (outbox mode Disabled)."));
        }

        // CQRCONF002: Transactional mode only routes a notification through the outbox while a unit-of-work
        // transaction is active. A plain IUnitOfWork (not IExplicitUnitOfWork) exposes no transaction state, so
        // every publish silently degrades to direct in-process dispatch.
        if (outbox.Mode == OutboxMode.Transactional)
        {
            var unitOfWork = services.GetService<IUnitOfWork>();
            if (unitOfWork is not null and not IExplicitUnitOfWork)
                issues.Add(new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Warning,
                    "CQRCONF002",
                    $"Outbox mode is 'Transactional' but the registered IUnitOfWork ('{unitOfWork.GetType().FullName}') " +
                    "does not implement IExplicitUnitOfWork, so no active transaction can be detected. Notifications will " +
                    "always dispatch directly in-process and never enter the outbox. Implement IExplicitUnitOfWork, or use a " +
                    "non-transactional outbox mode."));
        }

        // CQRCONF003: a handled notification without a stable [NotificationName] cannot be durably stored, so it
        // is dispatched in-process even though an outbox mode is enabled — a silent bypass of the outbox.
        if (outboxEnabled && notifications is not null)
            foreach (var notificationType in notifications.HandledNotificationTypes)
            {
                if (notifications.HasStableName(notificationType)) continue;

                issues.Add(new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Warning,
                    "CQRCONF003",
                    $"Notification '{notificationType.FullName}' has no stable name ([NotificationName]) while outbox mode is " +
                    $"'{outbox.Mode}', so it cannot be serialized for the outbox and will be dispatched in-process instead of " +
                    "through the outbox. Add [NotificationName] to route it through the outbox."));
            }

        // CQRCONF004: the generated registry is absent though there is work for it to wire up. This means
        // AddCqrsGenerated() was never called (or AddCqrs ran without it), so dispatch and diagnostics are broken.
        if (services.GetService<IRequestRegistry>() is null && requestBindings.Count > 0)
            issues.Add(new CqrsBindingIssue(
                CqrsBindingIssueSeverity.Error,
                "CQRCONF004",
                "No IRequestRegistry is registered although CQRSharp request bindings exist. The source-generated " +
                "registrations were not applied: call AddCqrsGenerated(...) instead of AddCqrs(...), and ensure it runs " +
                "before requests are dispatched."));

        // CQRCONF005 / CQRCONF006: a request opts into idempotency/retries via a marker interface, but the behavior
        // that honors it is not wired into the request's pipeline, so the marker silently has no effect.
        foreach (var binding in requestBindings)
        {
            var wired = binding.Pipeline.Concat(binding.ExemptedPipeline);

            if (typeof(IIdempotentRequest).IsAssignableFrom(binding.RequestType) &&
                !wired.Any(b => typeof(ICqrsIdempotencyBehaviorMarker).IsAssignableFrom(b.BehaviorType)))
                issues.Add(new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Warning,
                    "CQRCONF005",
                    $"Request '{binding.RequestType.FullName}' implements IIdempotentRequest, but no idempotency behavior " +
                    "is registered, so the marker has no effect (duplicate requests are NOT rejected). Enable it with " +
                    "UseIdempotency(...)."));

            if (typeof(IRetryableRequest).IsAssignableFrom(binding.RequestType) &&
                !wired.Any(b => typeof(ICqrsResilienceBehaviorMarker).IsAssignableFrom(b.BehaviorType)))
                issues.Add(new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Warning,
                    "CQRCONF006",
                    $"Request '{binding.RequestType.FullName}' implements IRetryableRequest, but no resilience behavior " +
                    "is registered, so the marker has no effect (failures are NOT retried). Enable it with UseResilience(...)."));
        }

        return issues;
    }
}
