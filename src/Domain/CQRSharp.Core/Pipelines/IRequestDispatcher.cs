using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Defines a unified mechanism for sending requests and receiving responses.
/// </summary>
public interface IRequestDispatcher
{
    /// <summary>
    ///     Sends a request and returns its corresponding response.
    /// </summary>
    /// <typeparam name="TResponse">The type of the response.</typeparam>
    /// <param name="request">The request object, which must implement <see cref="IRequest{TResponse}" />.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous send operation, containing the response.</returns>
    Task<TResponse> ExecuteAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sends a request by runtime type and returns the boxed response.
    /// </summary>
    Task<object?> ExecuteAsync(IRequest request, CancellationToken cancellationToken = default);
}