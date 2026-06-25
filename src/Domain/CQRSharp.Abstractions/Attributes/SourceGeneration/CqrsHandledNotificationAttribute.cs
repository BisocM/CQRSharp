namespace CQRSharp.Abstractions.Attributes.SourceGeneration;

/// <summary>
///     Emitted by the CQRSharp source generator, one per notification type that has at least one discovered handler,
///     as an assembly-level attribute. Tooling (the CQRSharp analyzers) reads these — across the current compilation
///     and referenced assemblies — to determine whether a notification has a subscriber.
/// </summary>
/// <remarks>
///     This is generated metadata; you should not normally apply it by hand.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class CqrsHandledNotificationAttribute(Type notificationType) : Attribute
{
    /// <summary>The notification type that has at least one handler.</summary>
    public Type NotificationType { get; } = notificationType;
}
