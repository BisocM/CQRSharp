using System.Diagnostics;
using CQRSharp.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

/// <summary>
///     Bounds a request's handler (and any unprioritized custom behavior inside it) by
///     <see cref="TimeoutOptions.Timeout" />: when the time runs out it cancels the token the handler receives and, once
///     the handler observes the cancellation, throws <see cref="RequestTimeoutException" />.
/// </summary>
/// <remarks>
///     The timeout is cooperative: a handler that ignores its token runs to completion, and its result is returned.
/// </remarks>
/// <typeparam name="TRequest">The type of the request being handled.</typeparam>
/// <typeparam name="TResult">The type of the result produced by the handler pipeline.</typeparam>
public sealed class TimeoutBehavior<TRequest, TResult>(
    ILogger<TimeoutBehavior<TRequest, TResult>> logger,
    IOptions<TimeoutOptions> options,
    TimeProvider? timeProvider = null) : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior where TRequest : IRequest
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request,
        RequestHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = PipelineTelemetry.StartActivity<TRequest>("Timeout.Guard");
        var timeout = options.Value.Timeout;
        activity?.SetTag(CqrsTelemetry.Tags.TimeoutMilliseconds, timeout.TotalMilliseconds);

        using var timeoutCancellationTokenSource = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellationTokenSource.Token);

        try
        {
            var result = await next(linkedCancellationTokenSource.Token).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (OperationCanceledException ex) when (timeoutCancellationTokenSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TimeoutLog.RequestTimedOut(logger, typeof(TRequest).Name, timeout.TotalMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Error, "Request timed out.");
            throw new RequestTimeoutException(typeof(TRequest), timeout, ex);
        }
    }

    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Timeout;
}
