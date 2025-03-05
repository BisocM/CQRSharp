namespace CQRSharp.Core.Caching.Pipelines;

/// <summary>
/// Provides access to a mapping from request types to precompiled pipeline builder delegates.
/// </summary>
public interface IPipelineRegistry
{
    /// <summary>
    /// Gets the read-only dictionary that maps request types to their corresponding
    /// precompiled pipeline builder delegates. This dictionary acts as a registry enabling
    /// efficient resolution of pipeline construction logic for different request types.
    /// </summary>
    IReadOnlyDictionary<Type, PipelineBuilderDelegate> PipelineMap { get; }
}