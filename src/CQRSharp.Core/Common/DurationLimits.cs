namespace CQRSharp.Pipelines;

/// <summary>
///     The longest span a duration option accepts: far longer than any schedule or retention needs, and safe to add to or
///     subtract from a clock reading, which <see cref="TimeSpan.MaxValue" /> is not.
/// </summary>
internal static class DurationLimits
{
    public static readonly TimeSpan Longest = TimeSpan.FromDays(3650);
}
