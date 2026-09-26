using System.Diagnostics;

namespace CQRSharp.Pipelines;

/// <summary>
///     Provides a single, static source and helper methods for creating pipeline-related activities for telemetry.
/// </summary>
internal static class PipelineTelemetry
{
    /// <summary>
    ///     A single ActivitySource for all CQRSharp pipeline operations.
    /// </summary>
    private static readonly ActivitySource Source = new(Core.Diagnostics.CqrsTelemetry.PipelinesActivitySourceName);

    /// <summary>
    ///     Starts a new activity for a pipeline operation and adds standard tags.
    /// </summary>
    /// <param name="activityName">The specific name for this activity (e.g., "UoW.Transaction").</param>
    /// <typeparam name="TRequest">The type of the request being processed, which the span is tagged with.</typeparam>
    /// <returns>A new <see cref="Activity" /> if listeners are enabled, otherwise null.</returns>
    internal static Activity? StartActivity<TRequest>(string activityName)
        where TRequest : IRequest
    {
        var activity = Source.StartActivity(activityName);

        // The same tag, in the same shape, as the dispatch span in CQRSharp.Core carries.
        activity?.SetTag(Core.Diagnostics.CqrsTelemetry.Tags.RequestType, Core.Diagnostics.CqrsTelemetry.TypeName(typeof(TRequest)));

        return activity;
    }
}
