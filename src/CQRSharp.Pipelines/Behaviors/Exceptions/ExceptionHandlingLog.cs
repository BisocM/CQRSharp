using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>The exception-handling behaviors' log messages, in the 4600 event-id block.</summary>
/// <remarks>
///     Information: the failure itself is the logging behavior's to report (it runs inside these behaviors, so it already
///     has); this records that a hook turned it into an answer, which is why the caller saw no exception.
/// </remarks>
internal static partial class ExceptionHandlingLog
{
    [LoggerMessage(4600, LogLevel.Information, "{RequestName} failed with {ExceptionType}; an exception handler supplied its result.")]
    public static partial void Handled(ILogger logger, string requestName, string exceptionType);

    [LoggerMessage(4601, LogLevel.Information, "Streaming {RequestName} failed with {ExceptionType}; an exception handler supplied the rest of the stream.")]
    public static partial void StreamHandled(ILogger logger, string requestName, string exceptionType);
}
