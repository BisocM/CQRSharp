using CQRSharp.Shared.Core.Data.Models.Requests;

namespace CQRSharp.Core.Caching.Requests;

/// <summary>
///     Defines a registry for managing handler and command type associations.
/// </summary>
public interface IRequestRegistry
{
    /// <summary>
    ///     Retrieves the handler type associated with the specified request type.
    /// </summary>
    /// <param name="requestType">The type of the request for which the handler type is sought.</param>
    /// <returns>The <see cref="Type" /> of the handler if found; otherwise, null.</returns>
    public Type? TryGetHandlerType(Type requestType);

    /// <summary>
    ///     Retrieves metadata associated with the specified request type.
    /// </summary>
    /// <param name="requestType">The type of the request for which metadata is retrieved.</param>
    /// <param name="metadata">Returned metadata.</param>
    /// <returns>The <see cref="RequestMetadata" /> instance if found; otherwise, null.</returns>
    public bool TryGetRequestMetadata(Type requestType, out RequestMetadata? metadata);
}