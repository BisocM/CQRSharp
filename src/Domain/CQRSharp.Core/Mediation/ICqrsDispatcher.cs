using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;

namespace CQRSharp.Core.Mediation;

/// <summary>
///     Primary CQRSharp dispatch façade: sends requests and publishes notifications.
/// </summary>
public interface ICqrsDispatcher
{
    /// <summary>
    ///     Sends a request through the configured pipeline and returns a response.
    /// </summary>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sends a request by runtime type (untyped), returning the boxed response.
    /// </summary>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Publishes a notification to all registered handlers.
    /// </summary>
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}
