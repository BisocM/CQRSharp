using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     A <see cref="FakeTimeProvider" /> that records the due time of every timer created from it. A test can then see
///     that a component waits on this clock, and for how long, before advancing it: a component that waited on the system
///     clock instead would create no timer here, rather than make the test wait in real time.
/// </summary>
internal sealed class RecordingTimeProvider : FakeTimeProvider
{
    private readonly List<TimeSpan> _timerDueTimes = [];

    /// <summary>The due time of each timer created so far, in creation order.</summary>
    public IReadOnlyList<TimeSpan> TimerDueTimes
    {
        get
        {
            lock (_timerDueTimes) return _timerDueTimes.ToArray();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timerDueTimes) _timerDueTimes.Add(dueTime);
        return base.CreateTimer(callback, state, dueTime, period);
    }
}
