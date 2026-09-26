using Microsoft.AspNetCore.Http;

namespace CQRSharp.AspNetCore;

/// <summary>
///     Configures which CQRSharp exceptions the exception handler registered by
///     <see cref="Microsoft.Extensions.DependencyInjection.CqrsProblemDetailsServiceCollectionExtensions.AddCqrsProblemDetails(Microsoft.Extensions.DependencyInjection.IServiceCollection)" />
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
    ///     Status code for <see cref="IdempotencyKeyMismatchException" /> (an idempotency key reused with a different
    ///     payload); <see langword="null" /> disables the mapping. Defaults to <c>422</c>.
    /// </summary>
    public int? IdempotencyKeyMismatchStatusCode { get; set; } = StatusCodes.Status422UnprocessableEntity;

    /// <summary>
    ///     Status code for <see cref="RateLimitExceededException" />; <see langword="null" /> disables the mapping.
    ///     Defaults to <c>429</c>. The response carries a <c>Retry-After</c> header with the exception's
    ///     <see cref="RateLimitExceededException.RetryAfter" /> (the time until the caller's token bucket holds a token
    ///     again), rounded up to whole seconds and at least one.
    /// </summary>
    public int? RateLimitStatusCode { get; set; } = StatusCodes.Status429TooManyRequests;

    /// <summary>
    ///     Status code for <see cref="RequestTimeoutException" />, which the timeout behavior (<c>UseTimeout(...)</c>)
    ///     throws; <see langword="null" /> disables the mapping. Defaults to <c>504</c>. Any other
    ///     <see cref="TimeoutException" /> (from a database driver, a Redis or HTTP client) is never mapped: it is left to
    ///     the host's error handling, which logs it.
    /// </summary>
    public int? TimeoutStatusCode { get; set; } = StatusCodes.Status504GatewayTimeout;

    /// <summary>
    ///     Status code for <see cref="BackgroundTaskRejectedException" />, which a <see cref="RunMode.Queued" /> dispatch
    ///     throws when the background task queue does not run the request (the queue is full, evicted it to make room for
    ///     newer work, or no longer accepts work because the host is shutting down); <see langword="null" /> disables the
    ///     mapping. Defaults to <c>503</c>. The request never started, so the client may send it again later.
    /// </summary>
    public int? BackgroundTaskRejectedStatusCode { get; set; } = StatusCodes.Status503ServiceUnavailable;

    /// <summary>
    ///     Status code for <see cref="InvalidIdempotencyKeyException" />; <see langword="null" /> disables the mapping.
    ///     Defaults to <c>400</c>.
    /// </summary>
    public int? InvalidIdempotencyKeyStatusCode { get; set; } = StatusCodes.Status400BadRequest;

    /// <summary>
    ///     The <c>Retry-After</c> (rounded up to whole seconds) sent with a duplicate-request response whose original
    ///     request is <em>still running</em> (<see cref="DuplicateRequestException.IsInProgress" />), telling the client to
    ///     ask again shortly. Once the original has completed, the retry gets its result replayed when that result can be
    ///     replayed (a plain <c>CommandResult</c>, or a result type configured with <c>ReplayResultsWith(...)</c>) and a
    ///     <c>409</c> otherwise; if the original fails, the retry runs the request again. Defaults to one second;
    ///     <see langword="null" /> sends no header. A duplicate whose original already completed never gets one.
    /// </summary>
    public TimeSpan? DuplicateInProgressRetryAfter { get; set; } = TimeSpan.FromSeconds(1);
}
