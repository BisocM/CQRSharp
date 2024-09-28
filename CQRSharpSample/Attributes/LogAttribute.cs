using CQRSharp.Core.Pipeline.Attributes;
using CQRSharp.Interfaces.Markers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharpSample.Attributes
{
    //Sample ICommandInterceptor implementation
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class LogAttribute : Attribute, ICommandInterceptor
    {
        public int Priority => 1;

        public Task OnAfterHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            //The service provider can be used here to get instances of any additional services.
            var logger = serviceProvider.GetRequiredService<ILogger<LogAttribute>>();

            logger.LogInformation($"[Attribute] Logging command ON EXIT {request.GetType().Name}");
            return Task.CompletedTask;
        }

        public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            //The service provider can be used here to get instances of any additional services.
            var logger = serviceProvider.GetRequiredService<ILogger<LogAttribute>>();

            logger.LogInformation($"[Attribute] Logging command ON ENTRY {request.GetType().Name}");
            return Task.CompletedTask;
        }
    }
}
