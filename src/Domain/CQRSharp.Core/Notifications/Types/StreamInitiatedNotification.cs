using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Represents a notification that is published when a streaming request is initiated.
/// </summary>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamInitiatedNotification<TItem> : INotification
{
    public StreamInitiatedNotification(IStreamRequest<TItem> request)
    {
        Request = request;
        RequestName = request.GetType().Name;
    }

    public IStreamRequest<TItem> Request { get; }

    public string RequestName { get; }
}
