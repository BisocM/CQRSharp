using CQRSharp.Pipelines;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     One of each failure the resilience behaviors treat as a verdict rather than a fault (besides caller cancellation,
///     which depends on the token): the cases of the "never retried" theories of both behaviors.
/// </summary>
internal static class RetryVerdicts
{
    public static TheoryData<string> Names =>
    [
        nameof(RateLimitExceededException),
        nameof(DuplicateRequestException),
        nameof(IdempotencyKeyMismatchException),
        nameof(RequestTimeoutException),
        nameof(RequestValidationException)
    ];

    public static Exception Create(string name, Type requestType) => name switch
    {
        nameof(RateLimitExceededException) => new RateLimitExceededException("Rate limit exceeded.", TimeSpan.FromSeconds(1)),
        nameof(DuplicateRequestException) => new DuplicateRequestException("key-1"),
        nameof(IdempotencyKeyMismatchException) => new IdempotencyKeyMismatchException("key-1"),
        nameof(RequestTimeoutException) => new RequestTimeoutException(requestType, TimeSpan.FromSeconds(1)),
        nameof(RequestValidationException) => new RequestValidationException(requestType, Array.Empty<ValidationFailure>()),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a retry verdict.")
    };
}
