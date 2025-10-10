using CQRSharp.Core.Options.Enums;

namespace CQRSharp.Core.Options;

/// <summary>
///     Provides configuration options for the notification outbox pattern.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>
    ///     Gets or sets the behavior mode for the notification outbox.
    ///     The default value is <see cref="OutboxMode.Transactional" />.
    /// </summary>
    public OutboxMode Mode { get; set; } = OutboxMode.Transactional;
}