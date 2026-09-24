using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     A <see cref="FakeTimeProvider" /> that records the due time of every timer created from it. A test can then see
///     that a component waits on this clock, and for how long, before advancing it: a component that waited on the system
///     clock instead would create no timer here, rather than make the test wait in real time. A component that works and
///     then waits on the clock in a loop has finished a round of work once it created that round's timer, which
///     <see cref="TimersCreatedAsync" /> tells a test without polling.
/// </summary>
internal sealed class RecordingTimeProvider : FakeTimeProvider
{
    private readonly List<TimeSpan> _timerDueTimes = [];
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

    public RecordingTimeProvider()
    {
    }

    public RecordingTimeProvider(DateTimeOffset startDateTime) : base(startDateTime)
    {
    }

    /// <summary>The due time of each timer created so far, in creation order.</summary>
    public IReadOnlyList<TimeSpan> TimerDueTimes
    {
        get
        {
            lock (_timerDueTimes) return _timerDueTimes.ToArray();
        }
    }

    /// <summary>Completes once <paramref name="count" /> timers in all have been created from this clock.</summary>
    public Task TimersCreatedAsync(int count)
    {
        lock (_timerDueTimes)
        {
            if (_timerDueTimes.Count >= count) return Task.CompletedTask;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, reached));
            return reached.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // The timer exists before a waiter hears of it, so advancing the clock right after the wait fires it.
        var timer = base.CreateTimer(callback, state, dueTime, period);
        List<TaskCompletionSource> reached = [];
        lock (_timerDueTimes)
        {
            _timerDueTimes.Add(dueTime);
            reached.AddRange(_waiters.Where(w => w.Count <= _timerDueTimes.Count).Select(w => w.Reached));
            _waiters.RemoveAll(w => w.Count <= _timerDueTimes.Count);
        }

        foreach (var waiter in reached) waiter.TrySetResult();
        return timer;
    }
}
