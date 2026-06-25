using CQRSharp.Core.Background.Outbox;

namespace CQRSharp.Core.Options;

/// <summary>
///     Provides configuration for the <see cref="OutboxProcessor" />.
/// </summary>
public sealed class OutboxProcessorOptions
{
    /// <summary>
    ///     Gets or sets the interval at which the outbox is polled for new messages.
    ///     Defaults to 5 seconds.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Gets or sets the maximum number of messages to process in a single batch.
    ///     Defaults to 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    ///     Gets or sets the number of times to retry processing a failed message before marking it as failed permanently.
    ///     Defaults to 3.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;
}