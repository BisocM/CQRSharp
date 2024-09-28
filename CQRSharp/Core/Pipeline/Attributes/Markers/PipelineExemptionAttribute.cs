namespace CQRSharp.Core.Pipeline.Attributes.Markers
{
    /// <summary>
    /// Marker class that allows the dispatcher to exempt a command from being processed by a specific pipeline.
    /// </summary>
    /// <param name="exemptedPipeline">The type of the pipeline to be exempt. Be mindful of generic typing.</param>
    [AttributeUsage(AttributeTargets.Class)]
    public class PipelineExemptionAttribute(Type exemptedPipeline) : Attribute
    {
        public Type ExemptedPipeline { get; } = exemptedPipeline;
    }
}