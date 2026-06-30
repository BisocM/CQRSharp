using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Pipelines.Behaviors.Logging;

/// <summary>
///     A pipeline behavior that logs the start, completion (with elapsed time), and failure of each request. Opt-in
///     via <c>AddLoggingBehavior()</c>; runs outermost so the measured time covers the whole pipeline.
/// </summary>
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
        logger.LogInformation("Handling {RequestName}", requestName);

        try
        {
            var result = await next(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Handled {RequestName} in {ElapsedMs:0.##}ms",
                requestName, _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Request {RequestName} failed after {ElapsedMs:0.##}ms",
                requestName, _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds);
            throw;
        }
    }

    public int PipelineExecutionPriority => CqrsPipelinePriorities.Logging;
}