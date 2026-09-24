using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>
///     The logging behaviors' log messages, in the 4000 event-id block. One id per outcome: 4000-4002, 4006, 4008 and 4009
///     for a request, 4003-4005, 4007, 4010 and 4011 for a stream.
/// </summary>
/// <remarks>
///     These are the only messages that report a request's failure at Error, with its exception; every other behavior
///     logs what it decided about the request, below Error. Which outcome a request's exception is, the
///     <see cref="RequestFailures" /> classification decides.
/// </remarks>
internal static partial class LoggingBehaviorLog
{
    [LoggerMessage(4000, LogLevel.Information, "Handling {RequestName}")]
    public static partial void Handling(ILogger logger, string requestName);

    [LoggerMessage(4001, LogLevel.Information, "Handled {RequestName} in {ElapsedMs:0.##}ms")]
    public static partial void Handled(ILogger logger, string requestName, double elapsedMs);

    [LoggerMessage(4002, LogLevel.Error, "Request {RequestName} failed after {ElapsedMs:0.##}ms")]
    public static partial void Failed(ILogger logger, Exception exception, string requestName, double elapsedMs);

    [LoggerMessage(4006, LogLevel.Information, "Request {RequestName} was canceled by the caller after {ElapsedMs:0.##}ms")]
    public static partial void Canceled(ILogger logger, string requestName, double elapsedMs);

    [LoggerMessage(4008, LogLevel.Information, "Request {RequestName} was rejected with {ExceptionType} after {ElapsedMs:0.##}ms")]
    public static partial void Rejected(ILogger logger, string requestName, string exceptionType, double elapsedMs);

    [LoggerMessage(4009, LogLevel.Warning, "Request {RequestName} could not be served: {ExceptionType} after {ElapsedMs:0.##}ms")]
    public static partial void Unavailable(ILogger logger, string requestName, string exceptionType, double elapsedMs);

    [LoggerMessage(4003, LogLevel.Information, "Streaming {RequestName}")]
    public static partial void Streaming(ILogger logger, string requestName);

    [LoggerMessage(4004, LogLevel.Information, "Streamed {RequestName}: {Count} item(s) in {ElapsedMs:0.##}ms")]
    public static partial void Streamed(ILogger logger, string requestName, long count, double elapsedMs);

    [LoggerMessage(4005, LogLevel.Error, "Streaming {RequestName} failed after {Count} item(s) and {ElapsedMs:0.##}ms")]
    public static partial void StreamFailed(ILogger logger, Exception exception, string requestName, long count, double elapsedMs);

    [LoggerMessage(4007, LogLevel.Information, "Streaming {RequestName} was canceled by the caller after {Count} item(s) and {ElapsedMs:0.##}ms")]
    public static partial void StreamCanceled(ILogger logger, string requestName, long count, double elapsedMs);

    [LoggerMessage(4010, LogLevel.Information, "Streaming {RequestName} was rejected with {ExceptionType} after {Count} item(s) and {ElapsedMs:0.##}ms")]
    public static partial void StreamRejected(ILogger logger, string requestName, string exceptionType, long count, double elapsedMs);

    [LoggerMessage(4011, LogLevel.Warning, "Streaming {RequestName} could not be served: {ExceptionType} after {Count} item(s) and {ElapsedMs:0.##}ms")]
    public static partial void StreamUnavailable(ILogger logger, string requestName, string exceptionType, long count, double elapsedMs);

    /// <summary>Logs how a request ended that threw <paramref name="exception" />, at the level its outcome calls for.</summary>
    public static void Ended(ILogger logger, Exception exception, CancellationToken cancellationToken, string requestName, double elapsedMs)
    {
        switch (RequestFailures.Classify(exception, cancellationToken))
        {
            case RequestFailureKind.Canceled:
                Canceled(logger, requestName, elapsedMs);
                break;
            case RequestFailureKind.Rejected:
                Rejected(logger, requestName, exception.GetType().Name, elapsedMs);
                break;
            case RequestFailureKind.Unavailable:
                Unavailable(logger, requestName, exception.GetType().Name, elapsedMs);
                break;
            default:
                Failed(logger, exception, requestName, elapsedMs);
                break;
        }
    }

    /// <summary>Logs how a stream ended that threw <paramref name="exception" />, at the level its outcome calls for.</summary>
    public static void StreamEnded(
        ILogger logger, Exception exception, CancellationToken cancellationToken, string requestName, long count, double elapsedMs)
    {
        switch (RequestFailures.Classify(exception, cancellationToken))
        {
            case RequestFailureKind.Canceled:
                StreamCanceled(logger, requestName, count, elapsedMs);
                break;
            case RequestFailureKind.Rejected:
                StreamRejected(logger, requestName, exception.GetType().Name, count, elapsedMs);
                break;
            case RequestFailureKind.Unavailable:
                StreamUnavailable(logger, requestName, exception.GetType().Name, count, elapsedMs);
                break;
            default:
                StreamFailed(logger, exception, requestName, count, elapsedMs);
                break;
        }
    }
}
