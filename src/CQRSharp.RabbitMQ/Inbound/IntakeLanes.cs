using System.Threading.Channels;
using CQRSharp.Transports;
using RabbitMQ.Client;

namespace CQRSharp.RabbitMQ;

/// <summary>A message a consumer received, held until it is acknowledged, rejected or returned on its own channel.</summary>
/// <param name="Channel">The channel it arrived on; its delivery tag means something on that channel only.</param>
/// <param name="DeliveryTag">Its delivery tag.</param>
/// <param name="Message">What is handed to the intake.</param>
/// <param name="PartitionKey">Its ordering key, which picks its lane.</param>
/// <param name="HasType">Whether it carried a <c>type</c> property (the notification name).</param>
/// <param name="ReceivedAt">When it arrived, from which the time it may be held is measured.</param>
internal sealed record ReceivedDelivery(IChannel Channel, ulong DeliveryTag, InboundNotification Message, string? PartitionKey, bool HasType, DateTime ReceivedAt);

/// <summary>
///     A consumer's lanes: the deliveries it received, taken in one lane at a time and several lanes side by side. A
///     delivery with a partition key always goes to the same lane, behind the earlier deliveries of that key, so a key's
///     order holds even while one of its messages is held back; one without a key goes to the least busy lane.
/// </summary>
internal sealed class IntakeLanes
{
    private readonly Channel<ReceivedDelivery>[] _lanes;
    private readonly int[] _depths;
    private readonly Task _completion;

    /// <param name="lanes">How many lanes.</param>
    /// <param name="capacity">How many deliveries a lane holds; the prefetch count, which bounds what the broker sends.</param>
    /// <param name="process">Takes one delivery in; never throws.</param>
    public IntakeLanes(int lanes, int capacity, Func<ReceivedDelivery, Task> process)
    {
        _lanes = new Channel<ReceivedDelivery>[lanes];
        _depths = new int[lanes];
        var workers = new Task[lanes];
        for (var i = 0; i < lanes; i++)
        {
            _lanes[i] = Channel.CreateBounded<ReceivedDelivery>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true
            });
            workers[i] = RunAsync(i, process);
        }

        _completion = Task.WhenAll(workers);
    }

    /// <summary>Completes once every lane has been completed and has worked through what it held.</summary>
    public Task Completion => _completion;

    /// <summary>
    ///     Queues a delivery on its lane; <see langword="false" /> once the lanes take nothing more. A lane holds as many
    ///     deliveries as the broker may have unacknowledged at once, so it is never full.
    /// </summary>
    public bool TryPost(ReceivedDelivery delivery)
    {
        var lane = Pick(delivery.PartitionKey);
        Interlocked.Increment(ref _depths[lane]);
        if (_lanes[lane].Writer.TryWrite(delivery)) return true;

        Interlocked.Decrement(ref _depths[lane]);
        return false;
    }

    /// <summary>Takes nothing more: each lane finishes what it holds, then ends.</summary>
    public void Complete()
    {
        foreach (var lane in _lanes)
            lane.Writer.TryComplete();
    }

    private int Pick(string? partitionKey)
    {
        if (partitionKey is not null)
            return (StringComparer.Ordinal.GetHashCode(partitionKey) & int.MaxValue) % _lanes.Length;

        var least = 0;
        for (var i = 1; i < _depths.Length; i++)
            if (Volatile.Read(ref _depths[i]) < Volatile.Read(ref _depths[least]))
                least = i;
        return least;
    }

    private async Task RunAsync(int lane, Func<ReceivedDelivery, Task> process)
    {
        // Each lane works on the thread pool, off the consumer's dispatch loop, which only queues.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await foreach (var delivery in _lanes[lane].Reader.ReadAllAsync().ConfigureAwait(false))
            try
            {
                await process(delivery).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _depths[lane]);
            }
    }
}
