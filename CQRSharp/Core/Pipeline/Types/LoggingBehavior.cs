using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipeline.Types
{
    public sealed class LoggingBehavior<TCommand, TResult>(ILogger<LoggingBehavior<TCommand, TResult>> logger) : IPipelineBehavior<TCommand, TResult>
    {
        public async Task<TResult> Handle(
            TCommand command,
            CancellationToken cancellationToken,
            Func<Task<TResult>> next)
        {
            //Get the commandName
            var commandName = typeof(TCommand).Name;

            logger.LogInformation("Handling {CommandName}", commandName);

            //TODO: Add command context data logging here, with sensitive data sanitization.

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await next();

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