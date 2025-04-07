using System.Collections.Concurrent;

namespace CQRSharp.Core.Caching.Pipelines;

/// <summary>
/// Represents a registry for managing and retrieving pipeline builder delegates
/// mapped to specific request types. The creation of the concurrent dictionary happens in the source code generation part of the library.
/// </summary>
public class PipelineRegistry(ConcurrentDictionary<Type, PipelineBuilderDelegate> pipelineMappings) : IPipelineRegistry
{
    /// <inheritdoc />
    public PipelineBuilderDelegate? GetPipelineBuilder(Type requestType)
    {
        pipelineMappings.TryGetValue(requestType, out var pipelineBuilder);
        return pipelineBuilder;
    }
}