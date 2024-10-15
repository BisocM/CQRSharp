using CQRSharp.Core.Options;
using CQRSharp.Interfaces.Markers.Request;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipeline.Types
{
    public sealed class ResilienceBehavior<TRequest, TResult>(
        ILogger<ResilienceBehavior<TRequest, TResult>> logger,
        DispatcherOptions options) : IPipelineBehavior<TRequest, TResult> where TRequest : RequestBase
    {
        public async Task<TResult> Handle(TRequest request, CancellationToken cancellationToken, Func<CancellationToken, Task<TResult>> next)
        {
            int retries = 0;

            while (true)
            {
                try
                {
                    return await next(cancellationToken);
                }
                catch (Exception ex) when (retries < options.MaxRetries)
                {
                    retries++;
                    logger.LogWarning(ex, "Retrying {CommandName} after failure", typeof(TRequest).Name);
                    await Task.Delay(1000, cancellationToken); //Slight delay before retrying
                }
            }
        }
    }
}
