using System.Diagnostics;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     The <see cref="ActivitySource" /> used by CQRSharp to emit distributed-tracing spans for request dispatch,
///     queued execution, and outbox delivery. Subscribe to it by name to collect spans (e.g. with OpenTelemetry:
///     <c>builder.AddSource(CqrsActivitySource.Name)</c>).
/// </summary>
public static class CqrsActivitySource
{
    /// <summary>The activity source name.</summary>
    public const string Name = "CQRSharp";

    /// <summary>The shared activity source instance.</summary>
    public static readonly ActivitySource Instance = new(Name);

    /// <summary>
    ///     Starts a request-scoped span for the given operation and request type, parented to the current activity.
    ///     Returns <c>null</c> when no listener is subscribed (zero overhead).
    /// </summary>
    internal static Activity? StartRequest(string operation, Type requestType)
    {
        var activity = Instance.StartActivity($"{operation} {requestType.Name}", ActivityKind.Internal);
        activity?.SetTag("cqrsharp.request_type", requestType.FullName);
        return activity;
    }
}
