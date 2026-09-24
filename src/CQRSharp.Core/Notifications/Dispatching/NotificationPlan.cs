using CQRSharp.Core.Pipelines;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     What a publish of <typeparamref name="TNotification" /> needs to know about one service provider, asked of it once
///     instead of on every publish: whom a notification of exactly that runtime type reaches, and which of the optional
///     steps (handlers registered by hand, pipeline behaviors) can be skipped because nothing is registered for them.
/// </summary>
/// <typeparam name="TNotification">The type a notification is delivered as.</typeparam>
internal sealed class NotificationPlan<TNotification> : ProviderPlan where TNotification : INotification
{
    /// <summary>The subscriptions a notification whose runtime type is <typeparamref name="TNotification" /> reaches.</summary>
    public required IReadOnlyList<NotificationSubscription> Subscriptions { get; init; }

    /// <summary><c>false</c> only when the provider proves nothing is registered as <c>INotificationHandler&lt;TNotification&gt;</c>.</summary>
    public required bool MayHaveHandRegisteredHandlers { get; init; }

    /// <summary><c>false</c> only when the provider proves no <c>INotificationPipelineBehavior&lt;TNotification&gt;</c> is registered.</summary>
    public required bool MayHaveBehaviors { get; init; }

    /// <summary>
    ///     Closed behaviors the generator discovered may be registered under the discovered-services key, so they are merged
    ///     with the application's own registrations.
    /// </summary>
    public required bool MergesDiscoveredBehaviors { get; init; }

    /// <summary>
    ///     The behaviors come from the closed set the module composition registered (a value-type notification on a runtime
    ///     without dynamic code) rather than from the open-generic registrations.
    /// </summary>
    public required bool UsesClosedBehaviors { get; init; }
}
