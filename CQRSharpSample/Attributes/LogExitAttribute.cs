using CQRSharp.Core.Pipeline.Attributes;
using CQRSharp.Interfaces.Markers.Request;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharpSample.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class LogExitAttribute(int priority) : Attribute, IPostHandlerAttribute
    {
        public int Priority => priority;

        public Task OnAfterHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            //The service provider can be used here to get instances of any additional services.
            var logger = serviceProvider.GetRequiredService<ILogger<LogEntryAttribute>>();

            logger.LogInformation($"[Attribute] Logging command ON EXIT {request.GetType().Name}");
            return Task.CompletedTask;
        }
    }
}
