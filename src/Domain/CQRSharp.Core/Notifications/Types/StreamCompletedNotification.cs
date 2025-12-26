using CQRSharp.Abstractions.Data.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Represents a notification that is published when a streaming request is fully consumed successfully.
/// </summary>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamCompletedNotification<TItem> : INotification
{
    public StreamCompletedNotification(IStreamRequest<TItem> request, long itemsYielded)
    {
        Request = request;
        RequestName = request.GetType().Name;
        ItemsYielded = itemsYielded;
    }

    public IStreamRequest<TItem> Request { get; }

    public string RequestName { get; }

    public long ItemsYielded { get; }
}
