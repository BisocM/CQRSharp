namespace CQRSharp.Core.Caching.Pipelines;

/// <summary>
/// Provides access to a mapping from request types to precompiled pipeline builder delegates.
/// </summary>
public interface IPipelineRegistry
{
    /// <summary>
    /// Retrieves the pipeline builder delegate associated with the specified request type.
    /// </summary>
    /// <param name="requestType">The type of the request for which the pipeline builder delegate is to be retrieved.</param>
    /// <returns>The pipeline builder delegate mapped to the provided request type.</returns>
    public PipelineBuilderDelegate? GetPipelineBuilder(Type requestType);
}

/// <summary>
/// Delegate that builds the execution pipeline for a given request.
/// It takes an IServiceProvider, the request as an object, a final‐handler delegate,
/// and a cancellation token, returning a Task that yields an object result.
/// </summary>
public delegate Task<object> PipelineBuilderDelegate(
    IServiceProvider services,
    object request,
    Func<CancellationToken, Task<object>> finalHandler,
    CancellationToken cancellationToken);