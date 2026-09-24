using CQRSharp.Pipelines;

namespace CQRSharp.Tests.Shared;

/// <summary>The outcomes the pipeline produces on purpose, each as the exception that carries it to the caller.</summary>
public enum PipelineOutcome
{
    Invalid,
    Duplicate,
    KeyReused,
    RateLimited,
    TimedOut,
    QueueRefused
}

/// <summary>
///     Creates the exception of a <see cref="PipelineOutcome" />, so the tests that pin how each outcome is logged (by the
///     logging behaviors and by CQRSharp.AspNetCore) name the same six.
/// </summary>
public static class PipelineOutcomes
{
    public static Exception Create(PipelineOutcome outcome) => outcome switch
    {
        PipelineOutcome.Invalid => new RequestValidationException(typeof(TestCommand), [new ValidationFailure("NAME_REQUIRED", "Name is required.", "Name")]),
        PipelineOutcome.Duplicate => new DuplicateRequestException("key-1", isInProgress: false),
        PipelineOutcome.KeyReused => new IdempotencyKeyMismatchException("key-1"),
        PipelineOutcome.RateLimited => new RateLimitExceededException("The rate limit for TestCommand was exceeded.", TimeSpan.FromSeconds(1)),
        PipelineOutcome.TimedOut => new RequestTimeoutException(typeof(TestCommand), TimeSpan.FromSeconds(2)),
        PipelineOutcome.QueueRefused => new BackgroundTaskRejectedException(BackgroundTaskRejectionReason.QueueFull, "The background task queue is full."),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };
}
