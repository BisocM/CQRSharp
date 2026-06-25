using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Interfaces.Notifications;

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
    ///     Executes a streaming request and returns an <see cref="IAsyncEnumerable{T}" /> of elements.
    /// </summary>
    IAsyncEnumerable<TItem> Stream<TItem>(IStreamRequest<TItem> request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Executes a streaming request by runtime type (untyped), returning boxed elements.
    /// </summary>
    IAsyncEnumerable<object?> Stream(object request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Publishes a notification to all registered handlers.
    /// </summary>
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}
