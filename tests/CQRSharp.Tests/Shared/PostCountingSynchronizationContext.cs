namespace CQRSharp.Tests.Shared;

/// <summary>
///     Stands in for a UI thread's <see cref="SynchronizationContext" />: it counts every continuation posted to it and
///     runs each on the thread pool, with itself current as a UI context runs its queue. A test starts library work under
///     it and checks nothing was posted back; a posted continuation still runs, so a test never deadlocks on it.
/// </summary>
public sealed class PostCountingSynchronizationContext : SynchronizationContext
{
    private int _posts;

    /// <summary>How many continuations were posted (or sent) to this context.</summary>
    public int Posts => Volatile.Read(ref _posts);

    public override void Post(SendOrPostCallback d, object? state)
    {
        Interlocked.Increment(ref _posts);
        ThreadPool.QueueUserWorkItem(static work => work.Context.Run(work.Callback, work.State), (Context: this, Callback: d, State: state), preferLocal: false);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        Interlocked.Increment(ref _posts);
        Run(d, state);
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Makes this the current context of the calling thread until the returned scope is disposed.</summary>
    public IDisposable Install()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        return new Restore(previous);
    }

    private void Run(SendOrPostCallback callback, object? state)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            callback(state);
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    private sealed class Restore(SynchronizationContext? previous) : IDisposable
    {
        public void Dispose() => SetSynchronizationContext(previous);
    }
}
