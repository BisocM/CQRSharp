using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>The resilience behaviors' log messages, in the 4300 event-id block.</summary>
/// <remarks>
///     The behaviors log what they decide, never the failure itself at Error. A failure that is retried is a Warning with
///     its exception: if the retry succeeds, nothing else ever reports it. Giving up is Information: the exception then
///     propagates, and the logging behavior (or the host) reports it once.
/// </remarks>
internal static partial class ResilienceLog
{
    [LoggerMessage(4300, LogLevel.Warning, "{RequestName} failed; retry {Attempt} of {MaxRetries} in {DelayMs}ms")]
    public static partial void Retrying(ILogger logger, Exception exception, string requestName, int attempt, int maxRetries, double delayMs);

    [LoggerMessage(4301, LogLevel.Information, "{RequestName} failed on all {Attempt} attempt(s); not retrying further")]
    public static partial void RetriesExhausted(ILogger logger, string requestName, int attempt);

    [LoggerMessage(4302, LogLevel.Debug, "{RequestName} failed with a non-retryable outcome ({Reason}); not retrying")]
    public static partial void NotRetried(ILogger logger, string requestName, string reason);

    [LoggerMessage(4303, LogLevel.Information, "Streaming {RequestName} failed after yielding items; not retrying, so no item is delivered twice")]
    public static partial void StreamFailedAfterItems(ILogger logger, string requestName);
}
