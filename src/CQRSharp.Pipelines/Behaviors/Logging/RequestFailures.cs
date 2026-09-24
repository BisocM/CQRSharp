namespace CQRSharp.Pipelines;

/// <summary>What a request that threw means to whoever reads the logs.</summary>
internal enum RequestFailureKind
{
    /// <summary>A failure nobody planned for: it is logged at Error, with the exception.</summary>
    Unexpected,

    /// <summary>The caller gave up (its token was cancelled): not a failure of the request.</summary>
    Canceled,

    /// <summary>Refused for something the caller controls: its input, a duplicate or a reused idempotency key, its rate limit.</summary>
    Rejected,

    /// <summary>The server could not serve it: it ran out of time, or the background task queue refused it.</summary>
    Unavailable
}

/// <summary>
///     Sorts a request's exception into a <see cref="RequestFailureKind" />, for the logging behaviors. An outcome the
///     pipeline produces on purpose is logged below Error and without its stack trace, which is the pipeline's own and
///     tells an operator nothing: a rejection at Information, since the caller has to act; an unavailable server at
///     Warning, since the operator may. CQRSharp.AspNetCore logs the same exceptions at the same levels when it maps
///     them to responses.
/// </summary>
internal static class RequestFailures
{
    public static RequestFailureKind Classify(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => RequestFailureKind.Canceled,
        RequestValidationException or DuplicateRequestException or IdempotencyKeyMismatchException or RateLimitExceededException
            => RequestFailureKind.Rejected,
        RequestTimeoutException or BackgroundTaskRejectedException => RequestFailureKind.Unavailable,
        _ => RequestFailureKind.Unexpected
    };
}
