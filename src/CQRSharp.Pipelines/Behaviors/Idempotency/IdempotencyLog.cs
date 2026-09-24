using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>
///     The idempotency behaviors' log messages, in the 4100 event-id block: 4100-4104 and 4109 the request behavior,
///     4105-4108 and 4110 the streaming one.
/// </summary>
/// <remarks>
///     A rejected duplicate or a reused key is what the behavior is for, so it is logged below Warning; the exception
///     reaches the caller, and the logging behavior reports the request's outcome. Warning is for a store or serializer
///     failure the behavior absorbs, which no one else reports.
/// </remarks>
internal static partial class IdempotencyLog
{
    [LoggerMessage(4100, LogLevel.Debug, "Replayed the stored result of {RequestName} for idempotency key {Key}.")]
    public static partial void Replayed(ILogger logger, string requestName, string key);

    [LoggerMessage(4101, LogLevel.Debug, "Rejected duplicate request {RequestName} with idempotency key {Key}.")]
    public static partial void Rejected(ILogger logger, string requestName, string key);

    [LoggerMessage(4102, LogLevel.Information, "Rejected request {RequestName}: idempotency key {Key} was already used by a request with a different payload.")]
    public static partial void Mismatch(ILogger logger, string requestName, string key);

    [LoggerMessage(4103, LogLevel.Warning, "Could not record the completed result of {RequestName} for idempotency key {Key}; a duplicate will be rejected rather than replayed.")]
    public static partial void CompletionNotStored(ILogger logger, Exception exception, string requestName, string key);

    [LoggerMessage(4104, LogLevel.Warning, "Could not release the idempotency key {Key} of the failed {RequestName}; a retry is rejected as a duplicate until the claim expires.")]
    public static partial void ReleaseFailed(ILogger logger, Exception exception, string requestName, string key);

    [LoggerMessage(4105, LogLevel.Debug, "Rejected duplicate streaming request {RequestName} with idempotency key {Key}.")]
    public static partial void StreamRejected(ILogger logger, string requestName, string key);

    [LoggerMessage(4106, LogLevel.Information, "Rejected streaming request {RequestName}: idempotency key {Key} was already used by a request with a different payload.")]
    public static partial void StreamMismatch(ILogger logger, string requestName, string key);

    [LoggerMessage(4107, LogLevel.Warning, "Could not record the completed streaming request {RequestName} for idempotency key {Key}; a duplicate is rejected either way.")]
    public static partial void StreamCompletionNotStored(ILogger logger, Exception exception, string requestName, string key);

    [LoggerMessage(4108, LogLevel.Warning, "Could not release the idempotency key {Key} of the failed streaming request {RequestName}; a retry is rejected as a duplicate until the claim expires.")]
    public static partial void StreamReleaseFailed(ILogger logger, Exception exception, string requestName, string key);

    [LoggerMessage(4109, LogLevel.Warning, "The idempotency result serializer {SerializerName} cannot store the result of {RequestName}, so its duplicates are rejected instead of replayed; for the JSON serializer, add the result type to the JsonSerializerContext. Reported once per request type and serializer.")]
    public static partial void ReplayUnavailable(ILogger logger, string requestName, string serializerName);

    [LoggerMessage(4110, LogLevel.Warning,
        "Disposing the stream of {RequestName}, which had already failed, failed as well; the stream's own failure is what its consumer receives.")]
    public static partial void StreamDisposalFailed(ILogger logger, Exception exception, string requestName);
}
