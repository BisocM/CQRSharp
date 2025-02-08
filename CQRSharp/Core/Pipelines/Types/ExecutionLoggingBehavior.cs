using System.Diagnostics;
using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines.Attributes;
using CQRSharp.Helpers;
using CQRSharp.Interfaces.Markers.Request;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipelines.Types;

/// <summary>
/// Represents a behavior in the pipeline that logs the execution of a command or a request.
/// This class is executed with the lowest priority among pipeline behaviors because it is intended solely for logging purposes.
/// </summary>
/// <remarks>
/// Logging includes command execution details such as the command name and sanitized context data.
/// It can also include JSON serialization for command context if implemented.
/// </remarks>
/// <typeparam name="TRequest">The type of the request being handled. It must derive from <see cref="RequestBase"/>.</typeparam>
/// <typeparam name="TResult">The type of the result returned by the request handler.</typeparam>
[PipelinePriority(int.MaxValue)] //Set this to the lowest priority, since all it does is log the execution of the command.
public sealed class ExecutionLoggingBehavior<TRequest, TResult>(
    ILogger<ExecutionLoggingBehavior<TRequest, TResult>> logger,
    LoggingOptions options) : IPipelineBehavior<TRequest, TResult> where TRequest : RequestBase
{
    /// <inheritdoc />
    public async Task<TResult> Handle(TRequest request,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        //Check if the command is null or not.
        ArgumentNullException.ThrowIfNull(request);

        //Get the commandName
        var commandName = typeof(TRequest).Name;

        logger.LogInformation("Handling {CommandName}", commandName);

        //TODO: Add JSON serialization for the context of the command.
        //TODO: Add command context data logging here, with sensitive data sanitization.
        var sanitizedExecutionContextString = CommandSanitizer.Sanitize(request, options);

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