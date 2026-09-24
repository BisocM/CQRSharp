using System.Collections.Concurrent;

namespace CQRSharp.Tests.Core;

/// <summary>
///     What the outbox delivery tests observe of the <see cref="ParallelProbe" /> deliveries: every attempt, the
///     deliveries that reached the handler and those that finished, and how many ran at once. A test can make every
///     delivery fail, fail until an attempt, or hold until it releases them.
/// </summary>
public sealed class DeliveryProbe
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource> _holds = [];
    private readonly List<(int Count, TaskCompletionSource Reached)> _enteredWaiters = [];
    private int _inFlightCount;

    public ConcurrentBag<ParallelProbe> Entered { get; } = new();
    public List<ParallelProbe> Completed { get; } = [];
    public ConcurrentQueue<int> Attempts { get; } = new();
    public bool HoldEachDelivery { get; set; }
    public int FailUntilAttempt { get; set; }

    /// <summary>Thrown by every delivery, after it is counted, until cleared.</summary>
    public Exception? Failure { get; set; }
    public int MaxConcurrency { get; private set; }

    /// <summary>Completes once <paramref name="count" /> deliveries in all have reached the handler, held or not.</summary>
    public Task EnteredAsync(int count)
    {
        lock (_gate)
        {
            if (Entered.Count >= count) return Task.CompletedTask;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _enteredWaiters.Add((count, reached));
            return reached.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var hold in _holds) hold.TrySetResult();
        }
    }

    // A held delivery observes the processor's stopping token, so a stop never waits on a hold the test forgot.
    public async Task DeliverAsync(ParallelProbe notification, CancellationToken cancellationToken)
    {
        Attempts.Enqueue(notification.Seq);
        if (Failure is { } failure)
            throw failure;
        if (Attempts.Count <= FailUntilAttempt - 1)
            throw new InvalidOperationException("probe failure");

        List<TaskCompletionSource> reached;
        lock (_gate)
        {
            _inFlightCount++;
            MaxConcurrency = Math.Max(MaxConcurrency, _inFlightCount);
            Entered.Add(notification);
            reached = _enteredWaiters.Where(w => w.Count <= Entered.Count).Select(w => w.Reached).ToList();
            _enteredWaiters.RemoveAll(w => w.Count <= Entered.Count);
        }

        foreach (var waiter in reached) waiter.TrySetResult();
        try
        {
            if (HoldEachDelivery)
            {
                TaskCompletionSource hold;
                lock (_gate)
                {
                    hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _holds.Add(hold);
                }

                await hold.Task.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            lock (_gate)
            {
                _inFlightCount--;
                Completed.Add(notification);
            }
        }
    }
}

[NotificationName("tests.parallel.probe", PartitionBy = nameof(Key))]
public sealed record ParallelProbe(int Seq, string? Key) : INotification;

public sealed class ParallelProbeHandler(DeliveryProbe probe) : INotificationHandler<ParallelProbe>
{
    public Task Handle(ParallelProbe notification, CancellationToken cancellationToken) => probe.DeliverAsync(notification, cancellationToken);
}
