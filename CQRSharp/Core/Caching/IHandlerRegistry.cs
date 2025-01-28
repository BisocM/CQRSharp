namespace CQRSharp.Core.Caching;

/// <summary>
///     Defines a registry for managing handler and command type associations.
/// </summary>
public interface IHandlerRegistry
{
    /// <summary>
    ///     Retrieves the handler type associated with the specified request type.
    /// </summary>
    /// <param name="requestType">The type of the request for which the handler type is sought.</param>
    /// <returns>The <see cref="Type" /> of the handler if found; otherwise, null.</returns>
    Type? GetHandlerType(Type requestType);

    /// <summary>
    ///     Retrieves metadata associated with the specified request type.
    /// </summary>
    /// <param name="requestType">The type of the request for which metadata is retrieved.</param>
    /// <returns>The <see cref="RequestMetadata" /> instance if found; otherwise, null.</returns>
    public RequestMetadata? GetMetadata(Type requestType);

    /// <summary>
    ///     Retrieves the command type associated with the given command name.
    /// </summary>
    /// <param name="commandName">The name of the command to look up.</param>
    /// <returns>The <see cref="Type" /> of the command if found; otherwise, null.</returns>
    Type? GetCommandTypeByName(string commandName);
}