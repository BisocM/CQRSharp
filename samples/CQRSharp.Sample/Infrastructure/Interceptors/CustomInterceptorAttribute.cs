using CQRSharp.Sample.Infrastructure.Logging;
using CQRSharp.Sample.Infrastructure.SelfTest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Infrastructure.Interceptors;

/// <summary>
///     A pre- and post-handler in one attribute, placed on the requests it applies to. It runs inside the pipeline, right
///     around the handler; the post-handler reads the outcome, so it can tell a returned result from a thrown exception.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CustomInterceptorAttribute(int priority) : Attribute, IPreHandlerAttribute, IPostHandlerAttribute
{
    public int PreHandlerExecutionPriority => priority;
    public int PostHandlerExecutionPriority => priority;

    public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        serviceProvider.GetRequiredService<SampleDiagnostics>().RecordInterceptorPre(request.GetType());
        return Task.CompletedTask;
    }

    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<CustomInterceptorAttribute>>();
        SampleLog.InterceptedRequest(logger, request.GetType().Name, outcome.Threw ? "threw" : "returned");

        serviceProvider.GetRequiredService<SampleDiagnostics>().RecordInterceptorPost(request.GetType(), outcome);
        return Task.CompletedTask;
    }
}
