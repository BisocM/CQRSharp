using System.Collections.Concurrent;
using RabbitMQ.Client;

namespace CQRSharp.RabbitMQ;

/// <summary>
///     The channels a transport publishes on, each rented by one send at a time: a channel is not meant to be shared by
///     concurrent publishes, and every one is opened with publisher confirms tracked per publish, so a publish completes
///     only once the broker confirmed it. There are at most as many as sends run at once (the outbox processor's degree of
///     parallelism). A channel that failed, or that belongs to a connection that is gone, is closed rather than reused.
/// </summary>
internal sealed class PublisherChannelPool : IAsyncDisposable, IDisposable
{
    private static readonly CreateChannelOptions ConfirmedChannel = new(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

    private readonly ConcurrentBag<IChannel> _idle = [];
    private volatile bool _disposed;

    /// <summary>An open channel on <paramref name="connection" /> for one send.</summary>
    public async Task<IChannel> RentAsync(IConnection connection, CancellationToken cancellationToken)
    {
        while (_idle.TryTake(out var channel))
        {
            if (channel.IsOpen) return channel;
            await CloseQuietlyAsync(channel).ConfigureAwait(false);
        }

        return await connection.CreateChannelAsync(ConfirmedChannel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Hands a channel back after a send that left it usable.</summary>
    public ValueTask ReturnAsync(IChannel channel)
    {
        if (_disposed || !channel.IsOpen) return CloseQuietlyAsync(channel);

        _idle.Add(channel);
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes a channel a send left in doubt (a publish that timed out, a channel-level error).</summary>
    public ValueTask DiscardAsync(IChannel channel) => CloseQuietlyAsync(channel);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        while (_idle.TryTake(out var channel))
            await CloseQuietlyAsync(channel).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposed = true;
        while (_idle.TryTake(out var channel))
            try
            {
                channel.Dispose();
            }
            catch (Exception)
            {
                // A channel that is already closed has nothing left to release.
            }
    }

    private static async ValueTask CloseQuietlyAsync(IChannel channel)
    {
        try
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A channel that is already closed has nothing left to release.
        }
    }
}
