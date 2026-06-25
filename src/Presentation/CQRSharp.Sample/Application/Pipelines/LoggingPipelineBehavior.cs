using System.Diagnostics;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Core.Pipelines;
using CQRSharp.Sample.Infrastructure.SelfTest;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Pipelines;

public sealed class LoggingPipelineBehavior<TRequest, TResult>(
    ILogger<LoggingPipelineBehavior<TRequest, TResult>> logger,
    SampleDiagnostics diagnostics)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior where TRequest : IRequest
{
    public int PipelineExecutionPriority => -100;

    public async Task<TResult> Handle(
        TRequest request,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            diagnostics.RecordLoggedRequest(typeof(TRequest));
            logger.LogInformation("[LoggingPipeline] Handling request {RequestName}", requestName);
            return await next(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LoggingPipeline] Request {RequestName} failed", requestName);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            logger.LogInformation(
                "[LoggingPipeline] Finished request {RequestName} in {ElapsedMilliseconds}ms",
                requestName,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
