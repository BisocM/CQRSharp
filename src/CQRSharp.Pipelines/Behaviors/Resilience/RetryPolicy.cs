namespace CQRSharp.Pipelines;

/// <summary>
///     The retry decision the command/query and streaming resilience behaviors share, so the two cannot drift: which
///     failures are verdicts that a retry would only delay, and which are faults worth another attempt.
/// </summary>
internal static class RetryPolicy
{
    /// <summary>
    ///     Whether <paramref name="failure" /> must be propagated without a retry, and why (for the span status).
    ///     Caller-initiated cancellation is terminal; any other <see cref="OperationCanceledException" /> (an
    ///     HttpClient timeout, a handler's own linked token) is the classic transient fault and is not. Likewise only the
    ///     timeout behavior's own <see cref="RequestTimeoutException" /> is terminal: any other
    ///     <see cref="TimeoutException" /> (a database driver, a Redis or HTTP client timing out) is transient.
    /// </summary>
    public static bool IsTerminal(Exception failure, CancellationToken cancellationToken, out string reason)
    {
        switch (failure)
        {
            case RateLimitExceededException:
                // The limiter said no; asking again only spends the back-off schedule on the same answer.
                reason = "Rate limit exceeded.";
                return true;
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                reason = "Operation canceled.";
                return true;
            case DuplicateRequestException:
                // The idempotency behavior runs inside this one. A duplicate is a verdict, not a fault: retrying it
                // only delays the rejection (and could run the work late if the original claim is released meanwhile).
                reason = "Duplicate request.";
                return true;
            case IdempotencyKeyMismatchException:
                // A key reused for a different payload is a client error; no retry can make it right.
                reason = "Idempotency key mismatch.";
                return true;
            case RequestTimeoutException:
                // The attempt already exceeded the request's time budget; retrying would multiply the budget by the
                // retry count.
                reason = "Timed out.";
                return true;
            case RequestValidationException:
                // Invalid input (of this request, or of one it dispatched) is the same input on every attempt.
                reason = "Validation failed.";
                return true;
            default:
                reason = string.Empty;
                return false;
        }
    }
}
