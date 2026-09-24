using Microsoft.Extensions.Logging;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The EF Core stores' log messages, source-generated, in the 6000 event-id block: 6000-6009 the outbox claim,
///     6010-6019 outbox retention, 6020-6029 the idempotency claim, 6030-6039 idempotency retention.
/// </summary>
internal static partial class EfCoreLog
{
    [LoggerMessage(6000, LogLevel.Debug, "Outbox claim lost a concurrency race on attempt {Attempt}/{MaxAttempts}; retrying.")]
    public static partial void ClaimRaceLost(ILogger logger, Exception exception, int attempt, int maxAttempts);

    [LoggerMessage(6001, LogLevel.Debug, "Outbox claim lost {MaxAttempts} concurrency races in a row; the contested messages are left for the next poll.")]
    public static partial void ClaimRacesExhausted(ILogger logger, Exception exception, int maxAttempts);

    [LoggerMessage(6002, LogLevel.Debug, "Handed back outbox message {MessageId} right after claiming it: another processor claimed a message of its partition at the same time.")]
    public static partial void ContestedClaimHandedBack(ILogger logger, Guid messageId);

    [LoggerMessage(6010, LogLevel.Information, "Purged {Count} processed outbox message(s) older than {Retention}.")]
    public static partial void ProcessedMessagesPurged(ILogger logger, int count, TimeSpan retention);

    [LoggerMessage(6011, LogLevel.Information, "Purged {Count} dead-lettered outbox message(s) older than {Retention}.")]
    public static partial void DeadLettersPurged(ILogger logger, int count, TimeSpan retention);

    [LoggerMessage(6012, LogLevel.Information, "Purged {Count} inbox record(s) older than {Retention}.")]
    public static partial void InboxRecordsPurged(ILogger logger, int count, TimeSpan retention);

    [LoggerMessage(6013, LogLevel.Warning, "Purging the outbox failed; it is tried again in {RetryIn}.")]
    public static partial void OutboxPurgeFailed(ILogger logger, Exception exception, TimeSpan retryIn);

    [LoggerMessage(6020, LogLevel.Debug, "Idempotency claim of {Key} lost an insert race on attempt {Attempt}/{MaxAttempts}; retrying.")]
    public static partial void IdempotencyInsertRaceLost(ILogger logger, Exception exception, string key, int attempt, int maxAttempts);

    [LoggerMessage(6021, LogLevel.Debug, "Idempotency take-over of {Key} lost a concurrency race on attempt {Attempt}/{MaxAttempts}; retrying.")]
    public static partial void IdempotencyTakeOverRaceLost(ILogger logger, Exception exception, string key, int attempt, int maxAttempts);

    [LoggerMessage(6030, LogLevel.Information, "Purged {Count} expired idempotency key(s).")]
    public static partial void ExpiredKeysPurged(ILogger logger, int count);

    [LoggerMessage(6031, LogLevel.Warning, "Purging expired idempotency keys failed; it is tried again in {RetryIn}.")]
    public static partial void IdempotencyPurgeFailed(ILogger logger, Exception exception, TimeSpan retryIn);
}
