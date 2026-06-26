using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Pipelines.Options;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Regression test for the token bucket's continuous (sub-second) refill: tokens accrue smoothly rather than only in
///     whole-second chunks. Driven by a <see cref="FakeTimeProvider" /> so virtual time advances deterministically — no
///     real waiting and no wall-clock flakiness.
/// </summary>
public class RateLimiterRefillTests
{
    [Fact(DisplayName = "Tokens replenish continuously within a sub-second window")]
    public void SubSecondRefill_ReplenishesTokens()
    {
        var time = new FakeTimeProvider();

        // 2 tokens/s == one token per 500ms.
        var limiter = new RateLimiter(Options.Create(new RateLimiterOptions
        {
            MaxTokens = 1,
            ReplenishRatePerSecond = 2,
            Scope = RateLimitScope.Global,
            MaxEntries = 16
        }), time);

        limiter.AllowRequest("user", typeof(RateLimiterRefillTests)).Should().BeTrue("the bucket starts full");
        limiter.AllowRequest("user", typeof(RateLimiterRefillTests)).Should().BeFalse("the only token was just consumed");

        // 400ms == 0.8 of a token: not yet enough, proving refill is continuous but not instantaneous.
        time.Advance(TimeSpan.FromMilliseconds(400));
        limiter.AllowRequest("user", typeof(RateLimiterRefillTests)).Should().BeFalse("only 0.8 tokens have accrued at 400ms");

        // A further 200ms (600ms total, still sub-second) crosses one whole token: the request is allowed again.
        time.Advance(TimeSpan.FromMilliseconds(200));
        limiter.AllowRequest("user", typeof(RateLimiterRefillTests)).Should().BeTrue("a full token replenished within a sub-second window");
    }
}
