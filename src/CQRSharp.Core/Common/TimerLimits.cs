namespace CQRSharp.Pipelines;

/// <summary>The longest delay <see cref="CancellationTokenSource" /> and <see cref="Task.Delay(TimeSpan)" /> accept.</summary>
internal static class TimerLimits
{
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
}
