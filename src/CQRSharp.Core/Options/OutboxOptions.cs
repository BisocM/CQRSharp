using CQRSharp.Core.Options.Enums;

namespace CQRSharp.Core.Options;

/// <summary>
///     Provides configuration options for the notification outbox pattern.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>
    ///     Gets or sets the behavior mode for the notification outbox. The default is
    ///     <see cref="OutboxMode.Disabled" /> — the outbox is off unless you explicitly enable it (e.g. via the
    ///     builder's <c>UseOutbox(...)</c> verb), so the stated default matches the actual runtime behavior.
    /// </summary>
    public OutboxMode Mode { get; set; } = OutboxMode.Disabled;
}