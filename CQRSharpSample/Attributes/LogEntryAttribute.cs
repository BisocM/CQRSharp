using CQRSharp.Core.Pipeline.Attributes;
using CQRSharp.Interfaces.Markers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharpSample.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class LogEntryAttribute(int priority) : Attribute, IPreHandlerAttribute
    {
        public int Priority => priority;

        public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            //The service provider can be used here to get instances of any additional services.
            var logger = serviceProvider.GetRequiredService<ILogger<LogEntryAttribute>>();

            logger.LogInformation($"[Attribute] Logging command ON ENTRY {request.GetType().Name}");
            return Task.CompletedTask;
        }
    }
}