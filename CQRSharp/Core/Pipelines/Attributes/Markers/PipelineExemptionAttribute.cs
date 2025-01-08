namespace CQRSharp.Core.Pipelines.Attributes.Markers;

/// <summary>
///     Marker class that allows the dispatcher to exempt a command from being processed by a specific pipeline.
/// </summary>
/// <param name="exemptedPipeline">The type of the pipeline to be exempt. Be mindful of generic typing.</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PipelineExemptionAttribute(Type exemptedPipeline) : Attribute
{
    /// <summary>
    /// Gets the type of the pipeline that is exempted from processing the associated command or request.
    /// </summary>
    /// <remarks>
    /// This property identifies the specific pipeline type that should be bypassed for the attributed command or request.
    /// It is primarily used by the dispatcher to determine which pipeline behaviors should be excluded during command or request processing.
    /// </remarks>
    public Type ExemptedPipeline { get; } = exemptedPipeline;
}