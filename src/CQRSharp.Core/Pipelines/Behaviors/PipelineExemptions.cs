namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Decides whether a pipeline behavior is exempted for a request by its <see cref="PipelineExemptionAttribute" />s.
///     The runtime filter and the diagnostics describer share it, so what the pipeline runs and what the diagnostics
///     report can never disagree.
/// </summary>
internal static class PipelineExemptions
{
    /// <summary>
    ///     Whether <paramref name="behaviorType" /> is named by any of <paramref name="exemptions" />, either exactly
    ///     (<c>typeof(MyBehavior&lt;Foo, Bar&gt;)</c>) or as its open generic definition (<c>typeof(MyBehavior&lt;,&gt;)</c>).
    /// </summary>
    public static bool IsExempted(Type behaviorType, ReadOnlySpan<PipelineExemptionAttribute> exemptions)
    {
        if (exemptions.Length == 0) return false;

        var definition = behaviorType.IsGenericType ? behaviorType.GetGenericTypeDefinition() : null;
        foreach (var exemption in exemptions)
            if (Matches(behaviorType, definition, exemption.ExemptedPipeline))
                return true;

        return false;
    }

    private static bool Matches(Type behaviorType, Type? behaviorDefinition, Type exemptedType)
        => exemptedType == behaviorType ||
           (exemptedType.IsGenericTypeDefinition && behaviorDefinition is not null && behaviorDefinition == exemptedType);
}
