using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;

namespace CQRSharp.Core.Requests;

/// <summary>
///     Defines a dispatcher interface for sending requests to their respective handlers.
/// </summary>
public interface IRequestDispatcher
{
    /// <summary>
    ///     Asynchronously executes a request (command or query) and returns its result.
    /// </summary>
    /// <param name="request">The request object to execute.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. The task result contains the output of the request,
    ///     which may be a <c>CommandResult</c> for commands or a specific result type for queries.
    /// </returns>
    Task<object?> ExecuteAsync(IRequest request, CancellationToken cancellationToken);
}