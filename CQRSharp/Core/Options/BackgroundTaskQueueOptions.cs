using System.Threading.Channels;

namespace CQRSharp.Core.Options;

/// <summary>
/// Represents configuration options for a background task queue,
/// allowing customization of capacity, behavior when the queue is full,
/// and an action to handle task rejection scenarios.
/// </summary>
public class BackgroundTaskQueueOptions
{
    /// <summary>
    /// Specifies the capacity of the queue. Set to zero or a negative value if you want to use an unbounded channel.
    /// </summary>
    public int Capacity { get; set; } = int.MaxValue;
    
    /// <summary>
    /// Specifies the strategy to use when the queue is full.
    /// </summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;
    
    /// <summary>
    /// Optional callback when a task is rejected or cannot be enqueued.
    /// </summary>
    public Action<Func<CancellationToken, Task>>? OnTaskRejected { get; set; }
}