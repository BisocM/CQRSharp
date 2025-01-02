using CQRSharp.Core.Pipelines;
using CQRSharp.Interfaces.Markers.Request;
using CQRSharp.Sample.Context;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Pipelines
{
    /// <summary>
    /// A custom pipeline behavior that demonstrates how to access the custom context fields
    /// from within the pipeline for logging or other logic.
    /// </summary>
    public class CustomContextLoggingBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
        where TRequest : RequestBase
    {
        private readonly ILogger<CustomContextLoggingBehavior<TRequest, TResult>> _logger;
        public CustomContextLoggingBehavior(ILogger<CustomContextLoggingBehavior<TRequest, TResult>> logger)
        {
            _logger = logger;
        }

        public async Task<TResult> Handle(TRequest request, Func<CancellationToken, Task<TResult>> next,
            CancellationToken cancellationToken)
        {
            // Check if request's context is our custom context
            if (request.Context is CustomRequestContext customContext)
            {
                _logger.LogInformation("Custom Context Detected: RequestId={RequestId}, UserId={UserId}, UserRole={UserRole}, SourceIp={SourceIp}",
                    customContext.RequestId, customContext.UserId, customContext.UserRole, customContext.SourceIp);
            }
            else
            {
                _logger.LogInformation("No custom context found, proceeding...");
            }

            return await next(cancellationToken);
        }
    }
}