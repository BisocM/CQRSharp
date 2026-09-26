using CQRSharp.Pipelines;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     A test implementation of <see cref="IRateLimitedContext" /> for rate-limiting tests, built by hand rather than by
///     a context factory, so it carries a fixed creation time instead of reading a clock.
/// </summary>
/// <param name="userId">The key the request is limited under.</param>
public class TestRateLimitedContext(string userId) : IRateLimitedContext
{
    /// <inheritdoc />
    public DateTime CreatedAt { get; } = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public string UserId { get; } = userId;
}
