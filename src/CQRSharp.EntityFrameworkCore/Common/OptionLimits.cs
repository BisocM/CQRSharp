namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The bounds the store options are validated against, so a value that would overflow the stores' clock arithmetic or
///     the retention timer fails at host start rather than on every claim or purge. The same bounds as CQRSharp.Core's
///     own options, which are internal to it.
/// </summary>
internal static class OptionLimits
{
    /// <summary>The longest duration: far longer than any lease or retention needs, and safe to add to a clock reading.</summary>
    public static readonly TimeSpan LongestDuration = TimeSpan.FromDays(3650);

    /// <summary>The longest delay <see cref="Task.Delay(TimeSpan)" /> accepts.</summary>
    public static readonly TimeSpan LongestTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public static bool IsDuration(TimeSpan value) => value > TimeSpan.Zero && value <= LongestDuration;
}
