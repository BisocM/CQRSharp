using System.Diagnostics;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     Retries a request implementing <see cref="IRetryableRequest" /> when it throws an exception another attempt might
///     change, up to <see cref="ResilienceOptions.MaxRetries" /> times with the configured back-off. A returned result,
///     a failed <see cref="CommandResult" /> included, is never retried. Any other request passes straight through,
///     untouched.
/// </summary>
/// <remarks>
///     A failure another attempt cannot change is never retried; see <see cref="IRetryableRequest" />. The behavior logs
///     each retry at Warning, with the failure it retries, and the final give-up at Information, but never the failure
///     itself at Error: that is the logging behavior's (or the host's) to report, once.
/// </remarks>
/// <typeparam name="TRequest">The type of the request.</typeparam>
/// <typeparam name="TResult">The type of the result returned after processing the request.</typeparam>
public sealed class ResilienceBehavior<TRequest, TResult>(
    ILogger<ResilienceBehavior<TRequest, TResult>> logger,
    IOptions<ResilienceOptions> options,
    TimeProvider? timeProvider = null) : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior, ICqrsResilienceBehaviorMarker
    where TRequest : IRequest
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        return request is IRetryableRequest ? HandleWithRetries(next, cancellationToken) : next(cancellationToken);
    }

    // Runs outside the unit of work, so each retry begins after the failed attempt was rolled back.
    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Resilience;

    private async Task<TResult> HandleWithRetries(RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var activity = PipelineTelemetry.StartActivity<TRequest>("Resilience.Operation");
        activity?.SetTag(CqrsTelemetry.Tags.MaxRetries, settings.MaxRetries);

        var retries = 0;
        while (true)
            try
            {
                var result = await next(cancellationToken).ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return result;
            }
            catch (Exception ex) when (RetryPolicy.IsTerminal(ex, cancellationToken, out var reason))
            {
                ResilienceLog.NotRetried(logger, typeof(TRequest).Name, reason);
                activity?.SetStatus(ActivityStatusCode.Error, reason);
                throw;
            }
            catch (Exception ex) when (retries < settings.MaxRetries)
            {
                retries++;
                var delay = settings.ComputeRetryDelay(retries);
                ResilienceLog.Retrying(logger, ex, typeof(TRequest).Name, retries, settings.MaxRetries, delay.TotalMilliseconds);

                var eventTags = new ActivityTagsCollection { { "exception.type", ex.GetType().Name } };
                activity?.AddEvent(new ActivityEvent($"RetryAttempt-{retries}", tags: eventTags));
                activity?.SetTag(CqrsTelemetry.Tags.RetryDelayMilliseconds, delay.TotalMilliseconds);

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                ResilienceLog.RetriesExhausted(logger, typeof(TRequest).Name, retries + 1);
                activity?.SetStatus(ActivityStatusCode.Error, "All retries exhausted.");
                throw;
            }
    }
}
