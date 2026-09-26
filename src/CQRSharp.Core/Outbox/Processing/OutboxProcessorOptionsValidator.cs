using CQRSharp.Pipelines;

namespace CQRSharp.Core.Outbox;

/// <summary>Rejects an <see cref="OutboxProcessorOptions" /> the processor cannot run with, at host start.</summary>
internal sealed class OutboxProcessorOptionsValidator : OptionsValidator<OutboxProcessorOptions>
{
    protected override IEnumerable<string> Failures(OutboxProcessorOptions options)
    {
        if (options.PollingInterval <= TimeSpan.Zero)
            yield return "OutboxProcessorOptions.PollingInterval must be greater than zero (a non-positive interval would hot-loop the processor).";
        if (options.PollingInterval > TimerLimits.MaxDelay)
            yield return "OutboxProcessorOptions.PollingInterval is longer than a timer can wait.";
        if (options.BatchSize <= 0)
            yield return "OutboxProcessorOptions.BatchSize must be greater than zero.";
        if (options.MaxAttempts < 1)
            yield return "OutboxProcessorOptions.MaxAttempts (the total number of delivery attempts, the first included) must be at least 1.";
        if (options.UnknownRecipientGracePeriod <= TimeSpan.Zero || options.UnknownRecipientGracePeriod > DurationLimits.Longest)
            yield return "OutboxProcessorOptions.UnknownRecipientGracePeriod must be greater than zero and must not exceed 10 years.";
        if (options.MaxDegreeOfParallelism < 1)
            yield return "OutboxProcessorOptions.MaxDegreeOfParallelism must be at least 1.";
        if (options.BacklogSampleInterval <= TimeSpan.Zero || options.BacklogSampleInterval > TimerLimits.MaxDelay)
            yield return "OutboxProcessorOptions.BacklogSampleInterval must be greater than zero and no longer than a timer can wait.";

        if (options.Retry is not { } retry)
        {
            yield return "OutboxProcessorOptions.Retry must not be null.";
            yield break;
        }

        if (retry.BaseDelay < TimeSpan.Zero)
            yield return "OutboxProcessorOptions.Retry.BaseDelay must not be negative.";
        if (retry.MaxDelay < retry.BaseDelay)
            yield return "OutboxProcessorOptions.Retry.MaxDelay must be at least BaseDelay.";
        if (retry.MaxDelay > DurationLimits.Longest)
            yield return "OutboxProcessorOptions.Retry.MaxDelay must not exceed 10 years.";
        if (!(retry.BackoffMultiplier >= 1 && double.IsFinite(retry.BackoffMultiplier)))
            yield return "OutboxProcessorOptions.Retry.BackoffMultiplier must be a finite number of at least 1.";
        if (retry.JitterFactor is not (>= 0 and < 1))
            yield return "OutboxProcessorOptions.Retry.JitterFactor must be in [0, 1).";
    }
}
