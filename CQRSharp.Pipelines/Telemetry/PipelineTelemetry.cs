using System.Diagnostics;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;

namespace CQRSharp.Pipelines.Telemetry;

/// <summary>
/// Provides a single, static source and helper methods for creating pipeline-related activities for telemetry.
/// </summary>
internal static class PipelineTelemetry
{
    /// <summary>
    /// A single ActivitySource for all CQRSharp pipeline operations.
    /// </summary>
    private static readonly ActivitySource Source = new("CQRSharp.Pipelines");

    /// <summary>
    /// Starts a new activity for a pipeline operation and adds standard tags.
    /// </summary>
    /// <param name="activityName">The specific name for this activity (e.g., "UoW.Transaction").</param>
    /// <param name="request">The request being processed, used for tagging.</param>
    /// <typeparam name="TRequest">The type of the request.</typeparam>
    /// <returns>A new <see cref="Activity"/> if listeners are enabled, otherwise null.</returns>
    internal static Activity? StartActivity<TRequest>(string activityName, TRequest request)
        where TRequest : IRequest
    {
        var activity = Source.StartActivity(activityName);

        // Add standard tags that are useful for any pipeline activity.
        activity?.SetTag("cqrsharp.request_type", typeof(TRequest).Name);
        
        return activity;
    }
}