using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Behaviors.RateLimiting;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Regression test for the token bucket's continuous (sub-second) refill. Previously tokens only replenished in
///     whole-second chunks, so a fast replenish rate could not recover within a sub-second window.
/// </summary>
public class RateLimiterRefillTests
{
    [Fact(DisplayName = "Tokens replenish within a sub-second window")]
    public async Task SubSecondRefill_ReplenishesTokens()
    {
        // 20 tokens/s == one token per 50ms. That gives a comfortable margin: the two back-to-back synchronous calls
        // below execute in microseconds (well under 50ms, so the bucket is still empty for the second), yet a sub-second
        // wait refills a full token. Before the fix, nothing refilled inside a sub-second window regardless of rate.
        var limiter = new RateLimiter(Options.Create(new RateLimiterOptions
        {
            MaxTokens = 1,
            ReplenishRatePerSecond = 20,
            Scope = RateLimitScope.Global,
            MaxEntries = 16
        }));

        limiter.AllowRequest("user", typeof(RateLimiterRefillTests)).Should().BeTrue("the bucket starts full");
        limiter.AllowRequest("user", typeof(RateLimiterRefillTests)).Should().BeFalse("the only token was just consumed");

        // 150ms at 20 tokens/s is three tokens' worth (capped at 1); before the fix nothing refilled sub-second.
        await Task.Delay(150);

        limiter.AllowRequest("user", typeof(RateLimiterRefillTests)).Should().BeTrue("tokens replenish continuously");
    }
}
