namespace CQRSharp.Core.Caching.Pipelines
{
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

    /// <summary>
    /// Default implementation of <see cref="IPipelineRegistry"/> that wraps a dictionary mapping request types to delegates.
    /// </summary>
    public class PipelineRegistry(IReadOnlyDictionary<Type, PipelineBuilderDelegate> pipelineMap)
        : IPipelineRegistry
    {
        /// <inheritdoc />
        public IReadOnlyDictionary<Type, PipelineBuilderDelegate> PipelineMap { get; } = pipelineMap;
    }
}