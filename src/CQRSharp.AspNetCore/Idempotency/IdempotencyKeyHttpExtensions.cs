using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;

namespace CQRSharp.AspNetCore;

/// <summary>
///     Reads and validates the <c>Idempotency-Key</c> request header so an endpoint can copy it onto an
///     <see cref="IIdempotentRequest" />.
/// </summary>
/// <remarks>
///     <para>
///         The helpers only read the header; nothing is assigned to a request implicitly. A key is valid when the
///         header appears exactly once and, after trimming whitespace and one pair of surrounding double quotes (the
///         IETF draft sends the key as a quoted structured-field string, most clients send it bare), it is non-empty,
///         no longer than the maximum length, and consists solely of visible ASCII characters other than <c>"</c>,
///         <c>\</c> and <c>,</c> (a comma is how HTTP folds repeated headers into one, so it would hide a second key).
///     </para>
///     <para>
///         The idempotency stores match keys with ordinal, case-sensitive equality, so the key is returned exactly
///         as sent (no case folding).
///     </para>
/// </remarks>
public static class IdempotencyKeyHttpExtensions
{
    /// <summary>The name of the request header: <c>Idempotency-Key</c>.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>
    ///     The default maximum accepted key length (255 characters). It sits inside the 450-character bound
    ///     <see cref="IIdempotentRequest.IdempotencyKey" /> documents, so an accepted key fits every store backend.
    /// </summary>
    public const int DefaultMaxLength = 255;

    /// <summary>
    ///     Attempts to read a valid <c>Idempotency-Key</c> header from the current request.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="idempotencyKey">The validated key when the method returns <see langword="true" />.</param>
    /// <param name="maxLength">The maximum accepted key length.</param>
    /// <returns>
    ///     <see langword="true" /> when the header is present and valid; <see langword="false" /> when it is missing
    ///     or malformed.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="httpContext" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxLength" /> is less than one.</exception>
    public static bool TryGetIdempotencyKey(this HttpContext httpContext,
        [NotNullWhen(true)] out string? idempotencyKey, int maxLength = DefaultMaxLength)
    {
        return Parse(httpContext, maxLength, out idempotencyKey) is null;
    }

    /// <summary>
    ///     Reads the <c>Idempotency-Key</c> header from the current request, rejecting the request when the header is
    ///     missing or malformed.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="maxLength">The maximum accepted key length.</param>
    /// <returns>The validated key.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="httpContext" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxLength" /> is less than one.</exception>
    /// <exception cref="InvalidIdempotencyKeyException">Thrown when the header is missing or malformed.</exception>
    public static string GetIdempotencyKey(this HttpContext httpContext, int maxLength = DefaultMaxLength)
    {
        return Parse(httpContext, maxLength, out var idempotencyKey) is { } error
            ? throw new InvalidIdempotencyKeyException(error)
            : idempotencyKey!;
    }

    // Returns null on success, otherwise a client-safe description of the problem (never echoing the header value).
    private static string? Parse(HttpContext httpContext, int maxLength, out string? idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        idempotencyKey = null;

        var values = httpContext.Request.Headers[HeaderName];
        if (values.Count == 0)
            return $"The {HeaderName} header is required.";

        // Two different keys on one request are ambiguous; picking either could dedupe against the wrong operation.
        if (values.Count > 1)
            return $"The {HeaderName} header must be sent exactly once.";

        var key = values[0].AsSpan().Trim();
        if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
            key = key[1..^1];

        if (key.IsEmpty)
            return $"The {HeaderName} header must not be empty.";

        if (key.Length > maxLength)
            return $"The {HeaderName} header must not exceed {maxLength} characters.";

        foreach (var c in key)
        {
            if (c is < '!' or > '~' or '"' or '\\' or ',')
                return $"The {HeaderName} header must contain only visible ASCII characters other than '\"', '\\' and ','.";
        }

        idempotencyKey = key.ToString();
        return null;
    }
}
