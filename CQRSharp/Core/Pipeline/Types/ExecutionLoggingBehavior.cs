using System.Diagnostics;
using CQRSharp.Core.Options;
using CQRSharp.Helpers;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipeline.Types
{
    public sealed class ExecutionLoggingBehavior<TExecutable, TResult>(
        ILogger<ExecutionLoggingBehavior<TExecutable, TResult>> logger,
        CQRSharpOptions options) : IPipelineBehavior<TExecutable, TResult>
    {
        public async Task<TResult> Handle(
            TExecutable executable,
            CancellationToken cancellationToken,
            Func<Task<TResult>> next)
        {
            //Check if the command is null or not.
            ArgumentNullException.ThrowIfNull(executable);

            //Get the commandName
            var commandName = typeof(TExecutable).Name;

            logger.LogInformation("Handling {CommandName}", commandName);

            //TODO: Add JSON serialization for the context of the command.
            //TODO: Add command context data logging here, with sensitive data sanitization.
            string sanitizedExecutionContextString = CommandSanitizer.Sanitize(executable, options);

            if (!string.IsNullOrEmpty(sanitizedExecutionContextString))
                logger.LogInformation(sanitizedExecutionContextString);

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