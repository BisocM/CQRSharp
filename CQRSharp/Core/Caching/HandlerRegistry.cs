using System.Collections.Concurrent;

namespace CQRSharp.Core.Caching;

/// <summary>
///     HandlerRegistry is responsible for maintaining the mapping between request types and their corresponding handler
///     types.
///     This allows for dynamic retrieval of handler types based on the request type received.
/// </summary>
public sealed class HandlerRegistry(ConcurrentDictionary<Type, RequestMetadata> handlerMappings) : IHandlerRegistry
{
    /// <inheritdoc />
    public Type? GetHandlerType(Type requestType)
    {
        handlerMappings.TryGetValue(requestType, out var metadata);
        return metadata?.HandlerType;
    }

    /// <inheritdoc />
    public RequestMetadata? GetMetadata(Type requestType)
    {
        handlerMappings.TryGetValue(requestType, out var metadata);
        return metadata;
    }

    /// <inheritdoc />
    public Type? GetCommandTypeByName(string commandName)
    {
        return handlerMappings.FirstOrDefault(x => x.Value.RequestType.Name == commandName).Key;
    }
}