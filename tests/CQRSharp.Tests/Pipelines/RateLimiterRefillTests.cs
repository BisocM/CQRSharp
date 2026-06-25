using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Pipelines.Options;
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
        // 2 tokens/s == one token per 500ms. The margin is deliberately wide so the test is robust on a loaded CI
        // runner: the two back-to-back synchronous calls below would have to be separated by a full 500ms scheduling
        // stall to spuriously refill (effectively impossible), yet a sub-second wait still accrues a whole token.
        // Before the fix tokens only replenished in whole-second chunks, so nothing recovered inside a sub-second window.
        var limiter = new RateLimiter(Options.Create(new RateLimiterOptions
        {
            MaxTokens = 1,
            ReplenishRatePerSecond = 2,
            Scope = RateLimitScope.Global,
            MaxEntries = 16
        }));

        limiter.AllowRequest("user", typeof(RateLimiterRefillTests)).Should().BeTrue("the bucket starts full");
        limiter.AllowRequest("user", typeof(RateLimiterRefillTests)).Should().BeFalse("the only token was just consumed");

        // 700ms at 2 tokens/s is more than one token's worth (capped at 1) and still comfortably under a second;
        // before the fix nothing refilled sub-second regardless of rate.
        await Task.Delay(700);

        limiter.AllowRequest("user", typeof(RateLimiterRefillTests)).Should().BeTrue("tokens replenish continuously");
    }
}