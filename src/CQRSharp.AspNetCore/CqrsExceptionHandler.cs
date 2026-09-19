using System.Globalization;
using System.Text.Json;
using CQRSharp.Pipelines;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.AspNetCore;

/// <summary>
///     Maps the known, user-safe CQRSharp exceptions to ProblemDetails responses and leaves every other exception
///     unhandled.
/// </summary>
internal sealed class CqrsExceptionHandler(IOptions<CqrsProblemDetailsOptions> options) : IExceptionHandler
{
    /// <summary>The ProblemDetails extension member carrying the validation failure codes, grouped like <c>errors</c>.</summary>
    internal const string ErrorCodesExtensionName = "errorCodes";

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var problemDetails = CreateProblemDetails(exception, settings);

        // Once the response has started the status line is already on the wire; let the host abort the request.
        if (problemDetails is null || httpContext.Response.HasStarted)
            return false;

        // The ProblemDetails writers serialize Status but do not apply it to the response.
        httpContext.Response.StatusCode = problemDetails.Status!.Value;

        var retryAfter = exception switch
        {
            RateLimitExceededException => settings.RateLimitRetryAfter,
            DuplicateRequestException { IsInProgress: true } => settings.DuplicateInProgressRetryAfter,
            _ => null
        };

        if (retryAfter is { } delay)
        {
            var seconds = Math.Max(1, (long)Math.Ceiling(delay.TotalSeconds));
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
            }))
        {
            return true;
        }

        await TypedResults.Problem(problemDetails).ExecuteAsync(httpContext);
        return true;
    }

    private static ProblemDetails? CreateProblemDetails(Exception exception, CqrsProblemDetailsOptions settings)
    {
        // Title and type stay unset so ASP.NET Core applies the RFC defaults for the status code. Only exceptions
        // whose message is written for the caller contribute it as the detail.
        switch (exception)
        {
            case RequestValidationException validation when settings.ValidationStatusCode is { } status:
                return CreateValidationProblemDetails(validation, status);

            case DuplicateRequestException when settings.DuplicateRequestStatusCode is { } status:
                return new ProblemDetails { Status = status, Detail = exception.Message };

            // The exception message embeds the internal request id and the user id, so a fixed detail is used.
            case RateLimitExceededException when settings.RateLimitStatusCode is { } status:
                return new ProblemDetails { Status = status, Detail = "Rate limit exceeded. Try again later." };

            // A TimeoutException can originate anywhere below the handler (a database driver, an HTTP client), so
            // its message is not known to be client-safe.
            case TimeoutException when settings.TimeoutStatusCode is { } status:
                return new ProblemDetails { Status = status, Detail = "The request timed out." };

            case InvalidIdempotencyKeyException when settings.InvalidIdempotencyKeyStatusCode is { } status:
                return new ProblemDetails { Status = status, Detail = exception.Message };

            default:
                return null;
        }
    }

    private static HttpValidationProblemDetails CreateValidationProblemDetails(
        RequestValidationException exception, int status)
    {
        // Failures that name no member are grouped under the empty key, the ASP.NET Core convention for
        // request-level errors.
        var messages = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var codes = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var failure in exception.Failures)
        {
            var member = failure.MemberName ?? string.Empty;
            if (!messages.TryGetValue(member, out var memberMessages))
            {
                messages[member] = memberMessages = [];
                codes[member] = [];
            }

            memberMessages.Add(failure.Message);
            codes[member].Add(failure.Code);
        }

        var problemDetails = new HttpValidationProblemDetails(
            messages.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal))
        {
            Status = status,
            Detail = exception.Message
        };

        problemDetails.Extensions[ErrorCodesExtensionName] = ToJsonElement(codes);
        return problemDetails;
    }

    // Extension members are serialized as object. A JsonElement is one of the few shapes the framework's
    // source-generated ProblemDetails JSON context can write without reflection, which a Dictionary<string, string[]>
    // is not - so the codes are pre-rendered to keep the response working under Native AOT.
    private static JsonElement ToJsonElement(Dictionary<string, List<string>> codes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (member, memberCodes) in codes)
            {
                writer.WriteStartArray(member);
                foreach (var code in memberCodes)
                    writer.WriteStringValue(code);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.GetBuffer().AsMemory(0, (int)stream.Length));
        return document.RootElement.Clone();
    }
}
