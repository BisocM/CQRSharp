using CQRSharp.Shared.Data.Attributes.Pipelines;
using CQRSharp.Shared.Data.Interfaces.Markers.Request;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public class CustomInterceptorAttribute(int priority) : Attribute, ICommandInterceptor
{
    public int PreHandlerExecutionPriority => priority;
    public int PostHandlerExecutionPriority => priority;

    public async Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        //Get the logger from the service provider
        var logger = serviceProvider.GetService<ILogger<CustomInterceptorAttribute>>();
        logger?.LogInformation(
            $"[PRE-HANDLER] Intercepting request of type {request.GetType().Name} with priority {PreHandlerExecutionPriority}");

        await Task.CompletedTask; //Simulate async pre-handler operation
    }

    public async Task OnAfterHandle(IRequest request, IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        //Get the logger from the service provider
        var logger = serviceProvider.GetService<ILogger<CustomInterceptorAttribute>>();
        logger?.LogInformation(
            $"[POST-HANDLER] Completed handling request of type {request.GetType().Name} with priority {PostHandlerExecutionPriority}");

        await Task.CompletedTask; //Simulate async post-handler operation
    }
}