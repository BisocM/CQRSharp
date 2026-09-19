using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Represents a notification that is published when a streaming request is fully consumed successfully.
/// </summary>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamCompletedNotification<TItem> : INotification
{
    /// <summary>
    ///     Creates a notification describing a streaming request that completed successfully.
    /// </summary>
    /// <param name="request">The streaming request that was consumed to completion.</param>
    /// <param name="itemsYielded">The total number of items the stream produced before finishing.</param>
    public StreamCompletedNotification(IStreamRequest<TItem> request, long itemsYielded)
    {
        Request = request;
        RequestName = request.GetType().Name;
        ItemsYielded = itemsYielded;
    }

    /// <summary>
    ///     The streaming request instance that was fully consumed.
    /// </summary>
    public IStreamRequest<TItem> Request { get; }

    /// <summary>
    ///     The runtime type name of the completed streaming request.
    /// </summary>
    public string RequestName { get; }

    /// <summary>
    ///     The total number of items yielded by the stream before it completed.
    /// </summary>
    public long ItemsYielded { get; }
}