using CQRSharp.Pipelines;

namespace CQRSharp.Core.BackgroundTasks;

/// <summary>Rejects a <see cref="BackgroundTaskQueueOptions" /> the queue cannot run with, at host start.</summary>
internal sealed class BackgroundTaskQueueOptionsValidator : OptionsValidator<BackgroundTaskQueueOptions>
{
    protected override IEnumerable<string> Failures(BackgroundTaskQueueOptions options)
    {
        if (options.Capacity <= 0)
            yield return "BackgroundTaskQueueOptions.Capacity must be greater than zero.";
        if (options.ConsumerStartTimeout <= TimeSpan.Zero || options.ConsumerStartTimeout > TimerLimits.MaxDelay)
            yield return "BackgroundTaskQueueOptions.ConsumerStartTimeout must be greater than zero and no longer than a timer can wait.";
        if (options.ShutdownTimeout != Timeout.InfiniteTimeSpan && (options.ShutdownTimeout < TimeSpan.Zero || options.ShutdownTimeout > TimerLimits.MaxDelay))
            yield return "BackgroundTaskQueueOptions.ShutdownTimeout must not be negative, nor longer than a timer can wait.";
        if (!Enum.IsDefined(options.FullMode))
            yield return "BackgroundTaskQueueOptions.FullMode must be a defined BoundedChannelFullMode.";
    }
}
