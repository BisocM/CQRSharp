using System.Diagnostics;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     The <see cref="ActivitySource" /> CQRSharp emits its request dispatch, queued execution and outbox delivery spans
///     on. Applications subscribe to it by name, <see cref="CqrsTelemetry.ActivitySourceName" />.
/// </summary>
internal static class CqrsActivitySource
{
    /// <summary>The activity source name.</summary>
    public const string Name = CqrsTelemetry.ActivitySourceName;

    /// <summary>The shared activity source instance.</summary>
    public static readonly ActivitySource Instance = new(Name, CqrsMetrics.Version);

    /// <summary>
    ///     Starts a request-scoped span for the given operation and request type, parented to the current activity.
    ///     Returns <c>null</c> when no listener is subscribed (zero overhead).
    /// </summary>
    public static Activity? StartRequest(string operation, Type requestType)
    {
        // Checked first so an untraced dispatch (the common case) does not pay to format a span name nobody reads.
        if (!Instance.HasListeners()) return null;

        var activity = Instance.StartActivity($"{operation} {requestType.Name}");
        activity?.SetTag(CqrsTelemetry.Tags.RequestType, CqrsTelemetry.TypeName(requestType));
        return activity;
    }
}
