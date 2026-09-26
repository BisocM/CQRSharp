namespace CQRSharp;

/// <summary>
///     Options for the notification outbox. The builder's <c>UseOutbox(...)</c> sets them; set <see cref="Mode" />
///     directly only when you register the outbox store yourself.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>
    ///     Which notifications go to the outbox. The default is
    ///     <see cref="OutboxMode.Disabled" />: the outbox is off unless you enable it (e.g. via the builder's
    ///     <c>UseOutbox(...)</c> verb).
    /// </summary>
    public OutboxMode Mode { get; set; } = OutboxMode.Disabled;
}
