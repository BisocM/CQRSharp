namespace CQRSharp;

/// <summary>
///     Exempts the attributed request (command, query or stream) from one pipeline behavior: that behavior does not run
///     when the request is dispatched. Declared on a base request class, it applies to derived requests too.
/// </summary>
/// <param name="exemptedPipeline">
///     The behavior type to skip: its open-generic definition (<c>typeof(LoggingBehavior&lt;,&gt;)</c>) or the form
///     closed over this request. The CQRA005 analyzer reports an exemption that can have no effect.
/// </param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class PipelineExemptionAttribute(Type exemptedPipeline) : Attribute
{
    /// <summary>The pipeline behavior type the attributed request is exempted from.</summary>
    public Type ExemptedPipeline { get; } = exemptedPipeline;
}