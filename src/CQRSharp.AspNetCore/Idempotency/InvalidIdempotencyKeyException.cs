using Microsoft.AspNetCore.Http;

namespace CQRSharp.AspNetCore;

/// <summary>
///     Thrown by <see cref="IdempotencyKeyHttpExtensions.GetIdempotencyKey" /> when the <c>Idempotency-Key</c> request
///     header is missing or malformed.
/// </summary>
/// <remarks>
///     Derives from <see cref="BadHttpRequestException" /> (status <c>400</c>) so hosts that already translate bad
///     requests keep working; the CQRSharp exception handler maps it to a ProblemDetails response. The message never
///     echoes the offending header value.
/// </remarks>
public sealed class InvalidIdempotencyKeyException : BadHttpRequestException
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="InvalidIdempotencyKeyException" /> class.
    /// </summary>
    /// <param name="message">A client-safe description of what is wrong with the header.</param>
    public InvalidIdempotencyKeyException(string message)
        : base(message, StatusCodes.Status400BadRequest)
    {
    }
}
