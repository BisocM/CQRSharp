using System.Diagnostics;
using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Options;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Pipelines.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines.Types;

/// <summary>
///     Represents a behavior that enforces a timeout on the execution of a pipeline request.
///     Implements <see cref="IPipelineBehavior{TRequest,TResult}" />.
/// </summary>
/// <typeparam name="TRequest">The type of the request being handled, must implement <see cref="IRequest" />.</typeparam>
/// <typeparam name="TResult">The type of the result produced by the handler pipeline.</typeparam>
public sealed class TimeoutBehavior<TRequest, TResult>(
    ILogger<TimeoutBehavior<TRequest, TResult>> logger,
    IOptions<TimeoutOptions> options) : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        // Creates a trace activity that guards the execution with a timeout.
        using var activity = PipelineTelemetry.StartActivity("Timeout.Guard", request);
        var timeout = options.Value.Timeout;

        // Adds the configured timeout duration to the trace for observability.
        activity?.SetTag("cqrsharp.timeout_ms", timeout.TotalMilliseconds);

        ArgumentNullException.ThrowIfNull(request);

        using var timeoutCancellationTokenSource = new CancellationTokenSource(timeout);
        var combinedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellationTokenSource.Token).Token;

        logger.LogInformation("Timeout for {ReqName} set for {TimeoutMilliseconds}ms", request.GetType().Name,
            timeout.TotalMilliseconds);

        try
        {
            var result = await next(combinedCancellationToken);
            // Mark the activity as successful if the operation completes in time.
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (OperationCanceledException) when (timeoutCancellationTokenSource.IsCancellationRequested)
        {
            logger.LogError("{CommandName} execution timed out", typeof(TRequest).Name);
            // Mark the activity as failed, indicating the timeout was exceeded.
            activity?.SetStatus(ActivityStatusCode.Error, "Request timed out.");
            throw new TimeoutException($"{typeof(TRequest).Name} execution timed out.");
        }
    }
}