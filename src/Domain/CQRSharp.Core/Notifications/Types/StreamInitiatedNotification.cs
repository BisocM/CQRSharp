using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Interfaces.Notifications;

namespace CQRSharp.Core.Notifications.Types;

/// <summary>
///     Represents a notification that is published when a streaming request is initiated.
/// </summary>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public sealed class StreamInitiatedNotification<TItem> : INotification
{
    /// <summary>
    ///     Creates a notification describing a streaming request that has just been initiated.
    /// </summary>
    /// <param name="request">The streaming request that is starting to stream.</param>
    public StreamInitiatedNotification(IStreamRequest<TItem> request)
    {
        Request = request;
        RequestName = request.GetType().Name;
    }

    /// <summary>
    ///     The streaming request instance that is being initiated.
    /// </summary>
    public IStreamRequest<TItem> Request { get; }

    /// <summary>
    ///     The runtime type name of the initiated streaming request.
    /// </summary>
    public string RequestName { get; }
}
