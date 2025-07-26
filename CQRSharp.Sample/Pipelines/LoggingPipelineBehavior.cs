using System.Diagnostics;
using System.Text.Json;
using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Pipelines;

// Use a high-priority value to ensure this runs early in the pipeline.
[PipelinePriority(-100)]
public class LoggingPipelineBehavior<TRequest, TResult>(ILogger<LoggingPipelineBehavior<TRequest, TResult>> logger)
    : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public async Task<TResult> Handle(TRequest request, Func<CancellationToken, Task<TResult>> next, CancellationToken cancellationToken)
    {
        // Check if this pipeline should be skipped for the current request.
        if (request.Metadata?.PipelineExemptions.Any(e => e.ExemptedPipeline == typeof(LoggingPipelineBehavior<TRequest, TResult>)) ?? false)
        {
            return await next(cancellationToken);
        }

        var requestName = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            logger.LogInformation("[LoggingPipeline] Handling request {RequestName}: {Request}", requestName, request);
            
            var result = await next(cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoggingPipeline] Request {RequestName} failed", requestName);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            logger.LogInformation("[LoggingPipeline] Finished request {RequestName} in {ElapsedMilliseconds}ms", requestName, stopwatch.ElapsedMilliseconds);
        }
    }
}