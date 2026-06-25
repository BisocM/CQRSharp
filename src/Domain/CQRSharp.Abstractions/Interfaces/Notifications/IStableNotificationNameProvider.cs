namespace CQRSharp.Abstractions.Interfaces.Notifications;

/// <summary>
///     Provides a trimming- and AOT-friendly way to determine whether a notification has a stable outbox name.
///     Implementations are typically source-generated from <c>[NotificationName]</c> usage.
/// </summary>
public interface IStableNotificationNameProvider
{
    /// <summary>
    ///     Attempts to get the stable name for a notification type.
    /// </summary>
    /// <param name="notificationType">The notification type.</param>
    /// <param name="stableName">The stable name if present.</param>
    /// <returns><c>true</c> when a stable name is known for the type; otherwise <c>false</c>.</returns>
    bool TryGetStableName(Type notificationType, out string stableName);
}

