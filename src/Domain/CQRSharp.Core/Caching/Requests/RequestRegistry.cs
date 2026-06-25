using System.Collections.Concurrent;
using CQRSharp.Abstractions.Models.Requests;

namespace CQRSharp.Core.Caching.Requests;

/// <summary>
///     HandlerRegistry is responsible for maintaining the mapping between request types and their corresponding handler
///     types.
///     This allows for dynamic retrieval of handler types based on the request type received.
///     HandlerMappings are populated in the source code generator.
/// </summary>
public sealed class RequestRegistry(ConcurrentDictionary<Type, RequestMetadata> handlerMappings) : IRequestRegistry
{
    /// <inheritdoc />
    public Type? TryGetHandlerType(Type requestType)
    {
        handlerMappings.TryGetValue(requestType, out var handlerType);
        return handlerType?.HandlerType;
    }

    /// <inheritdoc />
    public bool TryGetRequestMetadata(Type requestType, out RequestMetadata? metadata)
    {
        return handlerMappings.TryGetValue(requestType, out metadata);
    }
}