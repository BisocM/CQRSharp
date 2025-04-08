using CQRSharp.Core.Options;
using CQRSharp.Shared.Core.Data.Interfaces.Markers.Request;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipelines.Types;

/// <summary>
/// Represents a behavior that enforces a timeout on the execution of a pipeline request.
/// Implements <see cref="IPipelineBehavior{TRequest,TResult}"/>.
/// </summary>
/// <typeparam name="TRequest">The type of the request being handled, must implement <see cref="IRequest"/>.</typeparam>
/// <typeparam name="TResult">The type of the result produced by the handler pipeline.</typeparam>
public sealed class TimeoutBehavior<TRequest, TResult>(
    ILogger<TimeoutBehavior<TRequest, TResult>> logger,
    TimeoutOptions options) : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        //Check that the executable is not null
        ArgumentNullException.ThrowIfNull(request);

        using var timeoutCancellationTokenSource = new CancellationTokenSource(options.Timeout);
        var combinedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellationTokenSource.Token).Token;

        logger.LogInformation("Timeout for {ReqName} set for {TimeoutMilliseconds}ms", request.GetType().Name,
            options.Timeout.TotalMilliseconds);

        try
        {
            return await next(combinedCancellationToken);
        }
        catch (OperationCanceledException) when (timeoutCancellationTokenSource.IsCancellationRequested)
        {
            logger.LogError("{CommandName} execution timed out", typeof(TRequest).Name);
            throw new TimeoutException($"{typeof(TRequest).Name} execution timed out.");
        }
    }
}