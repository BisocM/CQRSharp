using CQRSharp.Core.Pipeline.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharpSample.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class LogEntryAttribute : Attribute, IPreCommandAttribute
    {
        public Task OnBeforeHandle(object command, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            //The service provider can be used here to get instances of any additional services.
            var logger = serviceProvider.GetRequiredService<ILogger<LogEntryAttribute>>();

            logger.LogInformation($"[Attribute] Logging command ON ENTRY {command.GetType().Name}");
            return Task.CompletedTask;
        }
    }
}