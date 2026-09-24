using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines;

/// <summary>
///     Logs the start, completion (with elapsed time) and failure of each request; enabled with <c>UseLogging()</c>.
///     It is the one place a failed request is logged at Error with its exception: the other built-in behaviors only log
///     what they decide.
/// </summary>
/// <remarks>
///     <para>
///         An outcome the pipeline produces on purpose is not an Error, and is logged in one line without the stack
///         trace: a request rejected for something its caller controls (<see cref="RequestValidationException" />,
///         <see cref="DuplicateRequestException" />, <see cref="IdempotencyKeyMismatchException" />,
///         <see cref="RateLimitExceededException" />) at Information, one the server could not serve
///         (<see cref="RequestTimeoutException" />, <see cref="BackgroundTaskRejectedException" />) at Warning, and one
///         its caller cancelled at Information.
///     </para>
///     <para>
///         It runs inside the exception-handling and rate-limiting behaviors, so a request throttled by its own limit is
///         not logged here, and outside everything else, so its elapsed time includes validation, retries and the unit
///         of work.
///     </para>
/// </remarks>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
public sealed class LoggingBehavior<TRequest, TResult>(
    ILogger<LoggingBehavior<TRequest, TResult>> logger, TimeProvider? timeProvider = null) : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var startTimestamp = _timeProvider.GetTimestamp();
        LoggingBehaviorLog.Handling(logger, requestName);

        try
        {
            var result = await next(cancellationToken).ConfigureAwait(false);
            LoggingBehaviorLog.Handled(logger, requestName, _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            LoggingBehaviorLog.Ended(logger, ex, cancellationToken, requestName, _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds);
            throw;
        }
    }

    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Logging;
}
