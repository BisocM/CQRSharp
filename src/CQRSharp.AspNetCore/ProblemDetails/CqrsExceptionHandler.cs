using System.Globalization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.AspNetCore;

/// <summary>
///     Maps the known, user-safe CQRSharp exceptions to ProblemDetails responses and leaves every other exception
///     unhandled.
/// </summary>
/// <remarks>
///     It logs every exception it maps, since on .NET 10 and later the exception-handler middleware does not log an
///     exception an <see cref="IExceptionHandler" /> reports as handled (on .NET 8 and 9 the middleware also logs it at
///     Error, with its stack trace, before this handler runs). Each is an outcome the pipeline produces on purpose, so it is
///     one line without the stack trace, at the level CQRSharp's logging behavior gives the same exception: Warning when
///     the server could not serve the request (<see cref="RequestTimeoutException" />,
///     <see cref="BackgroundTaskRejectedException" />), Information when the caller has to act, whatever status code it
///     is mapped to.
/// </remarks>
internal sealed partial class CqrsExceptionHandler(
    IOptions<CqrsProblemDetailsOptions> options,
    ILogger<CqrsExceptionHandler> logger) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var problemDetails = CreateProblemDetails(exception, settings);

        // Once the response has started the status line is already on the wire; let the host abort the request.
        if (problemDetails is null || httpContext.Response.HasStarted)
            return false;

        var status = problemDetails.Status!.Value;
        if (exception is RequestTimeoutException or BackgroundTaskRejectedException)
            LogMappedUnavailable(logger, exception.GetType().Name, status);
        else
            LogMappedRejected(logger, exception.GetType().Name, status);

        // The ProblemDetails writers serialize Status but do not apply it to the response.
        httpContext.Response.StatusCode = status;

        var retryAfter = exception switch
        {
            RateLimitExceededException rateLimited => rateLimited.RetryAfter,
            DuplicateRequestException { IsInProgress: true } => settings.DuplicateInProgressRetryAfter,
            _ => null
        };

        // Whole seconds, rounded up (a client that retries early is only rejected again), and at least one, since
        // Retry-After: 0 invites an immediate retry. Rounded in ticks: a delay's seconds as a double lose its last
        // ticks once the delay is long enough, and a delay one tick past a whole second would round down.
        if (retryAfter is { } delay)
        {
            var (wholeSeconds, remainder) = Math.DivRem(delay.Ticks, TimeSpan.TicksPerSecond);
            var seconds = Math.Max(1, remainder > 0 ? wholeSeconds + 1 : wholeSeconds);
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        // Prefer the host's IProblemDetailsService so its CustomizeProblemDetails / custom writers apply. It declines
        // when no writer accepts the request (e.g. an Accept header that excludes JSON); the response must still
        // describe the failure, so fall back to writing the same payload directly.
        var problemDetailsService = httpContext.RequestServices.GetService<IProblemDetailsService>();
        if (problemDetailsService is not null &&
            await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problemDetails,
                Exception = exception
            }).ConfigureAwait(false))
        {
            return true;
        }

        await TypedResults.Problem(problemDetails).ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }

    private static ProblemDetails? CreateProblemDetails(Exception exception, CqrsProblemDetailsOptions settings)
    {
        // Title and type stay unset so ASP.NET Core applies the RFC defaults for the status code. Only exceptions
        // whose message is written for the caller contribute it as the detail.
        switch (exception)
        {
            // The exception message names the server's request type, so the detail is the one a handler's
            // CommandResult.Invalid(...) carries by default: a client gets the same body whichever rejected the input.
            case RequestValidationException validation when settings.ValidationStatusCode is { } status:
                return ValidationProblemDetailsFactory.Create(validation.Failures, status, ValidationProblemDetailsFactory.DefaultDetail);

            case DuplicateRequestException when settings.DuplicateRequestStatusCode is { } status:
                return new ProblemDetails { Status = status, Detail = exception.Message };

            case IdempotencyKeyMismatchException when settings.IdempotencyKeyMismatchStatusCode is { } status:
                return new ProblemDetails { Status = status, Detail = exception.Message };

            // The exception message names the internal request type, so a fixed detail is used.
            case RateLimitExceededException when settings.RateLimitStatusCode is { } status:
                return new ProblemDetails { Status = status, Detail = "Rate limit exceeded. Try again later." };

            // Only the timeout behavior's own exception: a TimeoutException from anywhere else (a database driver, a
            // Redis or HTTP client, a Regex) is left to the host, which logs it as the failure it is. The message names
            // the internal request type, so a fixed detail is used.
            case RequestTimeoutException when settings.TimeoutStatusCode is { } status:
                return new ProblemDetails { Status = status, Detail = "The request timed out." };

            // The message describes the server's queue (its capacity and full mode), so a fixed detail is used.
            case BackgroundTaskRejectedException when settings.BackgroundTaskRejectedStatusCode is { } status:
                return new ProblemDetails { Status = status, Detail = "The server cannot take the request on right now. Try again later." };

            case InvalidIdempotencyKeyException when settings.InvalidIdempotencyKeyStatusCode is { } status:
                return new ProblemDetails { Status = status, Detail = exception.Message };

            default:
                return null;
        }
    }

    [LoggerMessage(7000, LogLevel.Information, "Mapped {ExceptionType} to a {StatusCode} ProblemDetails response")]
    private static partial void LogMappedRejected(ILogger logger, string exceptionType, int statusCode);

    [LoggerMessage(7001, LogLevel.Warning, "Mapped {ExceptionType} to a {StatusCode} ProblemDetails response: the server could not serve the request")]
    private static partial void LogMappedUnavailable(ILogger logger, string exceptionType, int statusCode);
}
