using System.Diagnostics;
using CQRSharp.Core.Options;
using CQRSharp.Helpers;
using CQRSharp.Interfaces.Markers;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipeline.Types
{
    public sealed class ExecutionLoggingBehavior<TRequest, TResult>(
        ILogger<ExecutionLoggingBehavior<TRequest, TResult>> logger,
        DispatcherOptions options) : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
    {
        public async Task<TResult> Handle(
            TRequest request,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task<TResult>> next)
        {
            //Check if the command is null or not.
            ArgumentNullException.ThrowIfNull(request);

            //Get the commandName
            var commandName = typeof(TRequest).Name;

            logger.LogInformation("Handling {CommandName}", commandName);

            //TODO: Add JSON serialization for the context of the command.
            //TODO: Add command context data logging here, with sensitive data sanitization.
            string sanitizedExecutionContextString = CommandSanitizer.Sanitize(request, options);

            if (!string.IsNullOrEmpty(sanitizedExecutionContextString))
                logger.LogInformation(sanitizedExecutionContextString);

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await next(cancellationToken);

                stopwatch.Stop();

                logger.LogInformation(
                    "Handled {CommandName} in {ElapsedMilliseconds}ms",
                    commandName,
                    stopwatch.ElapsedMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                logger.LogError(
                    ex,
                    "{CommandName} threw an exception after {ElapsedMilliseconds}ms",
                    commandName,
                    stopwatch.ElapsedMilliseconds);

                throw;
            }
        }
    }
}