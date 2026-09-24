using System.Security.Claims;

namespace CQRSharp.Sample.AspNetCore.Callers;

/// <summary>
///     The context of a request made on behalf of an HTTP caller. Implementing <see cref="IRateLimitedContext" /> opts the
///     request into the rate limit, one bucket per caller.
/// </summary>
public sealed class CallerContext : RequestContextBase, IRateLimitedContext
{
    public required string UserId { get; init; }
}

/// <summary>
///     Identifies the caller from the current HTTP request, never from the request body: the authenticated user when there
///     is one, the client's address otherwise. The source generator registers the factory.
/// </summary>
public sealed class CallerContextFactory(IHttpContextAccessor httpContextAccessor) : IRequestContextFactory<CallerContext>
{
    public ValueTask<CallerContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext
                   ?? throw new InvalidOperationException($"{request.GetType().Name} can only be dispatched while an HTTP request is handled.");

        return new ValueTask<CallerContext>(new CallerContext { UserId = CallerIdentity.Of(http) });
    }
}

/// <summary>
///     Who is calling, from the HTTP request alone: the authenticated user when there is one, the client's address
///     otherwise. One rule for the rate limit's buckets and the idempotency keys' scope.
/// </summary>
public static class CallerIdentity
{
    public static string Of(HttpContext http)
        => http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? $"address:{http.Connection.RemoteIpAddress}";
}
