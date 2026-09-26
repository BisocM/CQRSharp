using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>The timeout behaviors' log messages, in the 4400 event-id block.</summary>
/// <remarks>
///     Information: running out of time is the behavior's decision, and the <see cref="RequestTimeoutException" /> it
///     throws propagates. Each layer that handles it reports the outcome in its own line, with what it knows: the logging
///     behavior the request and its elapsed time, the ASP.NET Core exception handler the HTTP status it maps it to.
/// </remarks>
internal static partial class TimeoutLog
{
    [LoggerMessage(4400, LogLevel.Information, "{RequestName} timed out after {TimeoutMs}ms")]
    public static partial void RequestTimedOut(ILogger logger, string requestName, double timeoutMs);

    [LoggerMessage(4401, LogLevel.Information, "Streaming {RequestName} timed out after {TimeoutMs}ms")]
    public static partial void StreamTimedOut(ILogger logger, string requestName, double timeoutMs);
}
