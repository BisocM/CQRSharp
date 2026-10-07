using CQRSharp.Core.Modules;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using CQRSharp.Persistence;
using CQRSharp.Transports;

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

    /// <summary>
    ///     <c>CQRCONF013</c>: notification transports are registered while the outbox is off. A transport is fed through the
    ///     outbox, and what it receives is stored in it, so it would do nothing at all.
    /// </summary>
    public static CqrsBindingIssue TransportsWithoutOutbox(IEnumerable<INotificationTransport> transports)
        => new(
            CqrsBindingIssueSeverity.Error,
            "CQRCONF013",
            $"The notification transport(s) {NameList(transports.Select(t => t.Name))} are registered, but the outbox mode is " +
            "'Disabled'. A transport forwards what the outbox stores, and stores what it receives in the outbox, so nothing is " +
            "ever forwarded through it and nothing it receives reaches a handler. Turn the outbox on with UseOutbox(...), or " +
            "remove the transport.");

    /// <summary>
    ///     <c>CQRCONF014</c>: what a transport is configured to forward that can never be stored for it (a type the registered
    ///     serializer does not name, or, under the generated serializer, a name no module's notification has), and a type it
    ///     is configured to take in that the serializer does not name, so what arrives could never be read back as it. Empty
    ///     when every declared type and published name can be.
    /// </summary>
    public static IEnumerable<CqrsBindingIssue> UnnamedDeclarations(INotificationTransport transport, INotificationSerializer serializer)
    {
        foreach (var type in transport.Declaration.PublishedTypes)
            if (!serializer.TryGetNotificationName(type, out _))
                yield return TransportRoutesUnnamedType([transport.Name], type, serializer);

        foreach (var type in transport.Declaration.ConsumedTypes)
            if (!serializer.TryGetNotificationName(type, out _))
                yield return new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Error,
                    "CQRCONF014",
                    $"Transport '{transport.Name}' is configured to take in notification '{type.FullName}', but the registered " +
                    "notification serializer gives it no name, so nothing that arrives can be read back as it, and it cannot be " +
                    $"bound by name either. {UnnamedRemedy(type, serializer)}");

        // A custom serializer's names cannot be enumerated, so only the generated one's are checked.
        if (serializer is not CompositeOutboxNotificationSerializer generated) yield break;
        foreach (var name in transport.Declaration.PublishedNames)
            if (!generated.TryGetNotificationType(name, out _))
                yield return new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Error,
                    "CQRCONF014",
                    $"Transport '{transport.Name}' is configured to forward the notification name '{name}', but no notification " +
                    "type of the application's modules has that [NotificationName], so nothing is ever forwarded under it. Check " +
                    "the name, or reference the assembly that declares the notification.");
    }

    /// <summary>
    ///     <c>CQRCONF014</c> for one type: transports are configured to forward <paramref name="notificationType" />, which the
    ///     registered serializer does not name, so it can never be stored for them. A publish of it fails, where it
    ///     would otherwise stay in-process without a word.
    /// </summary>
    public static CqrsBindingIssue TransportRoutesUnnamedType(IEnumerable<string> transportNames, Type notificationType, INotificationSerializer serializer)
    {
        var remedy = UnnamedRemedy(notificationType, serializer);

        return new CqrsBindingIssue(
            CqrsBindingIssueSeverity.Error,
            "CQRCONF014",
            $"The notification transport(s) {NameList(transportNames)} are configured to forward notification " +
            $"'{notificationType.FullName}', but the registered notification serializer gives it no name, so it cannot be stored in the outbox, which is the only way to a " +
            $"transport: a publish of it fails rather than silently staying in-process. {remedy}");
    }

    /// <summary>
    ///     <c>CQRCONF015</c>: a transport whose name is not a valid outbox handler name, or is also the name of another transport
    ///     or of a notification handler, so a message addressed to it could reach the wrong recipient.
    /// </summary>
    public static IEnumerable<CqrsBindingIssue> TransportNameClashes(IReadOnlyList<INotificationTransport> transports, INotificationSubscriptionRegistry? subscriptions)
    {
        foreach (var transport in transports)
            if (string.IsNullOrWhiteSpace(transport.Name) || transport.Name.Length > MaxTransportNameLength)
                yield return new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Error,
                    "CQRCONF015",
                    $"The notification transport '{transport.GetType().FullName}' is named '{transport.Name}', which is not a valid " +
                    $"outbox handler name: it must be non-empty and at most {MaxTransportNameLength} characters.");

        foreach (var group in transports
                     .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                     .GroupBy(t => t.Name, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
            yield return new CqrsBindingIssue(
                CqrsBindingIssueSeverity.Error,
                "CQRCONF015",
                $"{group.Count()} notification transports are named '{group.Key}', so an outbox message addressed to that name is " +
                "ambiguous. Give each transport a unique name.");

        if (subscriptions is null) yield break;

        var handlers = subscriptions.Subscriptions.Select(s => s.HandlerName).ToHashSet(StringComparer.Ordinal);
        foreach (var transport in transports.Where(t => handlers.Contains(t.Name)).GroupBy(t => t.Name, StringComparer.Ordinal))
            yield return new CqrsBindingIssue(
                CqrsBindingIssueSeverity.Error,
                "CQRCONF015",
                $"The notification transport name '{transport.Key}' is also the name of a notification handler, so an outbox " +
                "message addressed to it is ambiguous. Rename the transport, or give the handler another [NotificationHandlerName].");
    }

    /// <summary>
    ///     <c>CQRCONF016</c> at a publish: under <see cref="OutboxMode.Transactional" />, a notification a transport forwards
    ///     was published with no transaction active. Such a publish is delivered in-process, where no transport can take it,
    ///     so its declared forwarding cannot be honored.
    /// </summary>
    public static CqrsBindingIssue ForwardedNotificationOutsideTransaction(Type notificationType, IEnumerable<INotificationTransport> transports)
        => new(
            CqrsBindingIssueSeverity.Error,
            "CQRCONF016",
            $"Notification '{notificationType.FullName}' is forwarded through the transport(s) {NameList(transports.Select(t => t.Name))}, " +
            "and it was published while the outbox mode is 'Transactional' with no transaction active. Such a publish is " +
            "delivered in-process, where no transport can take it, so it would never leave the process. Publish it inside a " +
            "transactional request, or use the Enabled mode (UseOutbox's default).");

    /// <summary>
    ///     <c>CQRCONF016</c> at startup: the outbox mode is <see cref="OutboxMode.Transactional" /> and transports forward
    ///     notifications, so a forwarded notification published outside a transaction fails. Whether one ever is depends on
    ///     the call sites, hence a warning.
    /// </summary>
    public static CqrsBindingIssue TransactionalOutboxWithForwarding(IEnumerable<INotificationTransport> transports)
        => new(
            CqrsBindingIssueSeverity.Warning,
            "CQRCONF016",
            $"The outbox mode is 'Transactional' and the transport(s) {NameList(transports.Select(t => t.Name))} forward " +
            "notifications: a forwarded notification published with no transaction active fails, since in-process delivery " +
            "could not forward it. Publish forwarded notifications inside transactional requests, or use the Enabled mode " +
            "(UseOutbox's default).");

    /// <summary>
    ///     <c>CQRCONF017</c>: what a transport is configured to take in that no local handler receives, so every one received
    ///     is acknowledged and dropped (or, for a name the generated serializer does not know, held back and then rejected).
    ///     Names are only checked under the generated serializer, whose names can be enumerated.
    /// </summary>
    public static IEnumerable<CqrsBindingIssue> ConsumedWithoutSubscribers(
        INotificationTransport transport,
        INotificationSerializer serializer,
        INotificationSubscriptionRegistry subscriptions)
    {
        // A type the serializer does not name is CQRCONF014, which says more than that nothing handles it.
        foreach (var type in transport.Declaration.ConsumedTypes)
            if (serializer.TryGetNotificationName(type, out _) && subscriptions.GetSubscriptions(type).Count == 0)
                yield return Unsubscribed(transport.Name, $"notification '{type.FullName}'");

        if (serializer is not CompositeOutboxNotificationSerializer generated) yield break;
        foreach (var name in transport.Declaration.ConsumedNames)
            if (!generated.TryGetNotificationType(name, out var type))
                yield return new CqrsBindingIssue(
                    CqrsBindingIssueSeverity.Warning,
                    "CQRCONF017",
                    $"Transport '{transport.Name}' takes in the notification name '{name}', but no notification type of the " +
                    "application's modules has that [NotificationName], so every one received is held back as unknown and then " +
                    "rejected. Check the name, or reference the assembly that declares the notification.");
            else if (subscriptions.GetSubscriptions(type).Count == 0)
                yield return Unsubscribed(transport.Name, $"notification '{type.FullName}' ('{name}')");

        static CqrsBindingIssue Unsubscribed(string transportName, string what)
            => new(
                CqrsBindingIssueSeverity.Warning,
                "CQRCONF017",
                $"Transport '{transportName}' takes in {what}, but no local handler receives it, so every one received is " +
                "acknowledged and dropped. Add a handler for it, or remove the binding.");
    }

    private static string UnnamedRemedy(Type notificationType, INotificationSerializer serializer)
        => serializer is CompositeOutboxNotificationSerializer
            ? notificationType.IsValueType
                ? "[NotificationName] applies to classes only: make it a class (or a record class)."
                : "Mark it with [NotificationName]."
            : $"The registered serializer '{serializer.GetType().FullName}' decides what is durable: have its TryGetNotificationName name the type.";

    /// <summary>The longest transport name: an outbox message's handler name, which the stores keep in 256 characters.</summary>
    private const int MaxTransportNameLength = 256;

    private static string NameList(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

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
