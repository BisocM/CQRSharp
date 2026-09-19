using CQRSharp.Pipelines;
using Microsoft.AspNetCore.Http;

namespace CQRSharp.AspNetCore;

/// <summary>
///     Configures which CQRSharp exceptions the exception handler registered by
///     <see cref="CqrsProblemDetailsServiceCollectionExtensions.AddCqrsProblemDetails(Microsoft.Extensions.DependencyInjection.IServiceCollection)" />
///     maps to ProblemDetails responses, and with which HTTP status code.
/// </summary>
/// <remarks>
///     Set a status code to <see langword="null" /> to disable that mapping: the exception is then left unhandled and
///     flows on to the next <see cref="Microsoft.AspNetCore.Diagnostics.IExceptionHandler" /> / the host's default
///     error handling.
/// </remarks>
public sealed class CqrsProblemDetailsOptions
{
    /// <summary>
    ///     Status code for <see cref="RequestValidationException" />; <see langword="null" /> disables the mapping.
    ///     Defaults to <c>400</c>.
    /// </summary>
    public int? ValidationStatusCode { get; set; } = StatusCodes.Status400BadRequest;

    /// <summary>
    ///     Status code for <see cref="DuplicateRequestException" />; <see langword="null" /> disables the mapping.
    ///     Defaults to <c>409</c>.
    /// </summary>
    public int? DuplicateRequestStatusCode { get; set; } = StatusCodes.Status409Conflict;

    /// <summary>
    ///     Status code for <see cref="RateLimitExceededException" />; <see langword="null" /> disables the mapping.
    ///     Defaults to <c>429</c>.
    /// </summary>
    public int? RateLimitStatusCode { get; set; } = StatusCodes.Status429TooManyRequests;

    /// <summary>
    ///     Status code for <see cref="TimeoutException" />; <see langword="null" /> disables the mapping.
    ///     Defaults to <c>504</c>.
    /// </summary>
    public int? TimeoutStatusCode { get; set; } = StatusCodes.Status504GatewayTimeout;

    /// <summary>
    ///     Status code for <see cref="InvalidIdempotencyKeyException" />; <see langword="null" /> disables the mapping.
    ///     Defaults to <c>400</c>.
    /// </summary>
    public int? InvalidIdempotencyKeyStatusCode { get; set; } = StatusCodes.Status400BadRequest;

    /// <summary>
    ///     When set, the value (rounded up to whole seconds) sent as the <c>Retry-After</c> header on a rate-limit
    ///     response. Defaults to <see langword="null" /> (no header).
    /// </summary>
    /// <remarks>
    ///     <see cref="RateLimitExceededException" /> does not carry the limiter's window, so the header cannot be
    ///     derived from the exception; set this to the window you configured for the rate-limiting behavior.
    /// </remarks>
    public TimeSpan? RateLimitRetryAfter { get; set; }
}
