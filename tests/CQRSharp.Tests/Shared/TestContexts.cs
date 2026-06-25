using CQRSharp.Pipelines.Behaviors.RateLimiting.Context;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     A test implementation of <see cref="IRateLimitedContext" /> for rate-limiting tests.
/// </summary>
/// <param name="requestId">The unique identifier for the request.</param>
/// <param name="userId">The identifier for the user initiating the request.</param>
public class TestRateLimitedContext(string requestId, string userId) : IRateLimitedContext
{
    /// <inheritdoc />
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <inheritdoc />
    public string RequestId { get; set; } = requestId;

    /// <inheritdoc />
    public string UserId { get; set; } = userId;
}