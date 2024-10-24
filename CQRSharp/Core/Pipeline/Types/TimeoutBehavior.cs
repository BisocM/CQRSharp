using CQRSharp.Core.Options;
using CQRSharp.Interfaces.Markers.Request;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipeline.Types
{
    public sealed class TimeoutBehavior<TRequest, TResult>(
        ILogger<TimeoutBehavior<TRequest, TResult>> logger,
        DispatcherOptions options) : IPipelineBehavior<TRequest, TResult> where TRequest : RequestBase
    {
        public async Task<TResult> Handle(
            TRequest request,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task<TResult>> next)
        {
            //Check that the executable is not null...
            ArgumentNullException.ThrowIfNull(request);

            using var timeoutCancellationTokenSource = new CancellationTokenSource(options.Timeout);
            var combinedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellationTokenSource.Token).Token;

            logger.LogInformation("Timeout for {ReqName} set for {TimeoutMilliseconds}ms", request.GetType().Name, options.Timeout.TotalMilliseconds);

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
}