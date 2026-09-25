using CQRSharp.Core.Modules;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using CQRSharp.Persistence;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     The <c>CQRCONF</c> rules that have a point of first use as well as a place in the startup report. Each rule has its
///     one owner here: the startup validator (through <see cref="CqrsConfigurationInspector" />) and the dispatch path
///     (the request plans and the notification publisher) ask the same question and get the same issue, with the same
///     code and message, so what host start reports is what the first dispatch or publish reports.
/// </summary>
internal static class CqrsConfigurationRules
{
    /// <summary>The <c>CQRCONF007</c> issue: a transactional outbox with no unit of work.</summary>
    public static CqrsBindingIssue TransactionalOutboxWithoutUnitOfWork { get; } = new(
        CqrsBindingIssueSeverity.Error,
        "CQRCONF007",
        "Outbox mode is 'Transactional' but no IUnitOfWork is registered, so there is never an active " +
        "transaction and no notification ever reaches the outbox: every one is dispatched in-process. Register a " +
        "unit of work (the builder's UseUnitOfWork(...), or UseEntityFrameworkCoreUnitOfWork<TContext>() for EF " +
        "Core), or use the Enabled mode (UseOutbox's default), which stores every named notification without one.");

    /// <summary>Whether <paramref name="requestType" /> carries a marker whose behavior <see cref="MissingMarkerBehaviors" /> checks.</summary>
    public static bool HasBehaviorMarkers(Type requestType)
        => typeof(IIdempotentRequest).IsAssignableFrom(requestType) || typeof(IRetryableRequest).IsAssignableFrom(requestType);

    /// <summary>
    ///     <c>CQRCONF005</c> / <c>CQRCONF006</c>: the markers of <paramref name="requestType" /> whose behavior is not
    ///     among <paramref name="wiredBehaviorTypes" />, the behaviors registered for the request. An exempted behavior
    ///     counts as wired: it is registered and deliberately skipped for the request, an explicit opt-out rather than a
    ///     gap. Empty when every marker is honored.
    /// </summary>
    /// <param name="requestType">The request type.</param>
    /// <param name="wiredBehaviorTypes">The concrete types of the behaviors registered for the request, exempted ones included.</param>
    public static IReadOnlyList<CqrsBindingIssue> MissingMarkerBehaviors(Type requestType, IReadOnlyCollection<Type> wiredBehaviorTypes)
    {
        List<CqrsBindingIssue>? issues = null;

        // An error: the marker is the request's contract that a repeated delivery is not processed twice, and without the
        // behavior every duplicate is processed.
        if (typeof(IIdempotentRequest).IsAssignableFrom(requestType) &&
            !wiredBehaviorTypes.Any(typeof(ICqrsIdempotencyBehaviorMarker).IsAssignableFrom))
            (issues ??= []).Add(new CqrsBindingIssue(
                CqrsBindingIssueSeverity.Error,
                "CQRCONF005",
                $"Request '{requestType.FullName}' implements IIdempotentRequest, but no idempotency behavior " +
                "is registered, so the marker has no effect (duplicate requests are NOT rejected). Enable it with " +
                "UseIdempotency(...)."));

        // A warning: without the behavior a failure is not retried, but nothing runs twice.
        if (typeof(IRetryableRequest).IsAssignableFrom(requestType) &&
            !wiredBehaviorTypes.Any(typeof(ICqrsResilienceBehaviorMarker).IsAssignableFrom))
            (issues ??= []).Add(new CqrsBindingIssue(
                CqrsBindingIssueSeverity.Warning,
                "CQRCONF006",
                $"Request '{requestType.FullName}' implements IRetryableRequest, but no resilience behavior " +
                "is registered, so the marker has no effect (failures are NOT retried). Enable it with UseResilience(...)."));

        return issues ?? (IReadOnlyList<CqrsBindingIssue>)[];
    }

    /// <summary>
    ///     <c>CQRCONF003</c> / <c>CQRCONF010</c> for one handled notification type while an outbox mode is on: why the
    ///     registered serializer gives it no name, so it cannot be stored and is dispatched in-process. <c>CQRCONF010</c>
    ///     (an error) when it lost its <c>[NotificationName]</c> to another module's type; <c>CQRCONF003</c> (a warning)
    ///     when it simply has none; <see langword="null" /> when the serializer names it, or when, under the generated
    ///     serializer, it is dispatched in-process by what it is.
    /// </summary>
    /// <param name="notificationType">A concrete notification type that has subscriptions.</param>
    /// <param name="serializer">The registered notification serializer.</param>
    /// <param name="mode">The outbox mode in effect (not <see cref="OutboxMode.Disabled" />).</param>
    public static CqrsBindingIssue? UnnamedHandledNotification(Type notificationType, INotificationSerializer serializer, OutboxMode mode)
    {
        if (serializer.TryGetNotificationName(notificationType, out _)) return null;

        if (serializer is CompositeOutboxNotificationSerializer generated)
        {
            if (generated.Conflicts.FirstOrDefault(c => c.Types.Contains(notificationType)) is { } conflict)
                return NameConflict(conflict);

            // Under the generated serializer, two kinds of notification are dispatched in-process by what they are, not
            // through an omission this could point at: a value type ([NotificationName] applies to classes only), and
            // CQRSharp's own lifecycle notifications, which an application handles but cannot annotate. A custom
            // serializer can name either, so it is still asked.
            if (notificationType.IsValueType || notificationType.Assembly == typeof(CommandCompletedNotification).Assembly)
                return null;
        }

        // The generated serializer names exactly the [NotificationName] types; a custom one names what it chooses.
        var remedy = serializer is CompositeOutboxNotificationSerializer
            ? "Add [NotificationName] to route it through the outbox."
            : $"The registered serializer '{serializer.GetType().FullName}' replaces the generated one, so [NotificationName] " +
              "alone does not help: have its TryGetNotificationName name the type to route it through the outbox.";

        return new CqrsBindingIssue(
            CqrsBindingIssueSeverity.Warning,
            "CQRCONF003",
            $"Notification '{notificationType.FullName}' has handlers but no stable name while outbox mode is " +
            $"'{mode}': the registered notification serializer does not name it, so it cannot be stored in the " +
            $"outbox and will be dispatched in-process instead. {remedy}");
    }

    /// <summary>
    ///     <c>CQRCONF010</c>: every stable name the generated serializer's modules give to more than one notification type.
    ///     Empty under a custom serializer, which names what it chooses.
    /// </summary>
    public static IEnumerable<CqrsBindingIssue> NotificationNameConflicts(INotificationSerializer serializer)
        => serializer is CompositeOutboxNotificationSerializer generated
            ? generated.Conflicts.Select(NameConflict)
            : [];

    /// <summary>
    ///     <c>CQRCONF011</c>: handlers registered by hand for a durable notification have no outbox subscription, so a
    ///     publish that goes through the outbox never reaches them.
    /// </summary>
    /// <param name="notificationType">The durable notification type.</param>
    /// <param name="name">The name the serializer stores it under.</param>
    /// <param name="byHand">The types of the handlers registered by hand for it; not empty.</param>
    public static CqrsBindingIssue HandRegisteredHandlersOfDurableNotification(Type notificationType, string name, IEnumerable<Type> byHand)
        => new(
            CqrsBindingIssueSeverity.Warning,
            "CQRCONF011",
            $"Notification '{notificationType.FullName}' is durable (stored as '{name}'), but the handler(s) registered " +
            $"by hand for it ({string.Join(", ", byHand.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal))}) " +
            "have no outbox subscription, so a publish that goes through the outbox never reaches them; they run only " +
            "when it is dispatched in-process. Declare them in an assembly the source generator runs in, where they are " +
            "discovered and subscribed.");

    /// <summary>
    ///     <c>CQRCONF012</c>: the handlers registered by hand for a durable notification cannot be constructed, so whether
    ///     <c>CQRCONF011</c> applies cannot be told.
    /// </summary>
    public static CqrsBindingIssue UnresolvableHandRegisteredHandlers(Type notificationType, Exception failure)
        => new(
            CqrsBindingIssueSeverity.Error,
            "CQRCONF012",
            $"The notification handlers registered by hand for '{notificationType.FullName}' could not be resolved: " +
            $"{failure.Message} Publishing it in-process fails the same way.");

    /// <summary>The message of the exception a dispatch or publish fails with when an error-level rule is broken.</summary>
    public static string FailureMessage(CqrsBindingIssue issue)
        => $"CQRSharp configuration error {issue.Code}: {issue.Message}";

    private static CqrsBindingIssue NameConflict(CompositeOutboxNotificationSerializer.NameConflict conflict)
        => new(
            CqrsBindingIssueSeverity.Error,
            "CQRCONF010",
            $"The notification name '{conflict.Name}' is given to more than one notification type " +
            $"({string.Join(", ", conflict.Types.Select(t => t.FullName))}), so an outbox message stored under it " +
            "cannot be read back as the type it was stored as. Give each type a unique [NotificationName].");
}
