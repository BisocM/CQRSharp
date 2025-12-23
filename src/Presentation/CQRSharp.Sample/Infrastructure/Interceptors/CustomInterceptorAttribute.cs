using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Infrastructure.Interceptors;

[AttributeUsage(AttributeTargets.Class)]
public class CustomInterceptorAttribute(int priority) : Attribute, ICommandInterceptor
{
    public int PreHandlerExecutionPriority => priority;
    public int PostHandlerExecutionPriority => priority;

    public async Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var logger = serviceProvider.GetService<ILogger<CustomInterceptorAttribute>>();
        logger?.LogInformation(
            "[PRE-HANDLER] Intercepting request of type {RequestType} with priority {Priority}",
            request.GetType().Name, PreHandlerExecutionPriority);

        serviceProvider.GetService<SampleDiagnostics>()?.RecordInterceptorPre(request.GetType());
        await Task.CompletedTask;
    }

    public async Task OnAfterHandle(IRequest request, IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var logger = serviceProvider.GetService<ILogger<CustomInterceptorAttribute>>();
        logger?.LogInformation(
            "[POST-HANDLER] Completed handling request of type {RequestType} with priority {Priority}",
            request.GetType().Name, PostHandlerExecutionPriority);

        serviceProvider.GetService<SampleDiagnostics>()?.RecordInterceptorPost(request.GetType());
        await Task.CompletedTask;
    }
}
