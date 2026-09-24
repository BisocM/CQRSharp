namespace CQRSharp;

/// <summary>
///     Pins the stable name under which a notification handler subscribes to the outbox. Every outbox message is
///     addressed to one handler by name, so the name must stay the same for as long as messages addressed to it may
///     still be in the store. Without this attribute the name is the handler type's namespace-qualified name, which
///     changes when the type is renamed or moved. A message addressed to a name no handler carries is deferred for the
///     outbox processor's unknown-recipient grace period, then dead-lettered.
/// </summary>
/// <param name="name">A stable identifier for the handler; unique among the application's notification handlers.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NotificationHandlerNameAttribute(string name) : Attribute
{
    /// <summary>The stable identifier for the handler (at most 256 characters for the relational outbox stores).</summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
}
