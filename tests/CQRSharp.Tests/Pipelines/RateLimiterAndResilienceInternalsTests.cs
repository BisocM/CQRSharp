using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Pipelines.Options;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Deepens branch coverage for the rate limiter and resilience internals that the model-level behavior tests do not
///     reach: token-bucket burst/capacity boundaries, "just enough"/zero-token edges, concurrent acquisition, the LRU
///     cache's reuse / capacity / idle-eviction paths (exercised through the public <see cref="RateLimiter" /> surface,
///     since the cache types are <c>internal</c> to CQRSharp.Pipelines and not visible to the test assembly), and the
///     full branch table of <see cref="ResilienceOptions.ComputeRetryDelay" />.
/// </summary>
public class RateLimiterAndResilienceInternalsTests
{
    private static RateLimiterOptions BaseOptions(int maxTokens, double rate, RateLimitScope scope = RateLimitScope.Global)
        => new()
        {
            MaxTokens = maxTokens,
            ReplenishRatePerSecond = rate,
            Scope = scope,
            MaxEntries = 64,
            // Disable both the timer (CleanupInterval == Zero) and the per-request idle sweep (MaxIdleTime == Zero)
            // unless a test opts in, so unrelated tests stay perfectly deterministic.
            CleanupInterval = TimeSpan.Zero,
            MaxIdleTime = TimeSpan.Zero
        };

    private static RateLimiter NewLimiter(RateLimiterOptions options) => new(Options.Create(options));

    // ---------------------------------------------------------------------------------------------------------------
    // RateLimiter / TokenBucket edge cases
    // ---------------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Burst boundary: a full bucket allows exactly MaxTokens requests then blocks")]
    public void Burst_AllowsExactlyMaxTokens_ThenBlocks()
    {
        // A very slow replenish rate means no meaningful refill happens across these synchronous calls, so the bucket
        // starts full at MaxTokens and drains one token per call. This pins the capacity boundary precisely.
        using var limiter = NewLimiter(BaseOptions(5, 0.0001));

        for (var i = 0; i < 5; i++)
            limiter.AllowRequest("burst-user", typeof(RateLimiterAndResilienceInternalsTests))
                .Should().BeTrue($"token #{i + 1} of the initial burst should be available");

        limiter.AllowRequest("burst-user", typeof(RateLimiterAndResilienceInternalsTests))
            .Should().BeFalse("the bucket is empty once all MaxTokens have been consumed");
    }

    [Fact(DisplayName = "Single-token bucket: first request consumes the only token, second is denied")]
    public void SingleToken_JustEnoughThenZero()
    {
        using var limiter = NewLimiter(BaseOptions(1, 0.0001));

        limiter.AllowRequest("u", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeTrue("the lone token is available");
        limiter.AllowRequest("u", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeFalse("the lone token was just spent");
    }

    [Fact(DisplayName = "Fractional refill below 1.0 keeps the bucket empty (tokens < 1 branch)")]
    public void FractionalRefill_BelowOneToken_StaysBlocked()
    {
        // 2 tokens/s == one token per 500ms. After draining the single token and advancing the clock only 50ms, well
        // under 0.1 of a token has accrued, so _tokens stays < 1 and the request is denied. Driving the bucket through a
        // FakeTimeProvider keeps this deterministic — a real-clock wait could let a whole token accrue under CI load and
        // flip the assertion. This exercises the "refilled but still below the 1-token threshold" branch of TryConsume.
        var time = new FakeTimeProvider();
        using var limiter = new RateLimiter(Options.Create(BaseOptions(1, 2)), time);

        limiter.AllowRequest("frac", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeTrue();
        limiter.AllowRequest("frac", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeFalse();

        time.Advance(TimeSpan.FromMilliseconds(50));

        limiter.AllowRequest("frac", typeof(RateLimiterAndResilienceInternalsTests))
            .Should().BeFalse("a sub-threshold partial refill must not grant a whole token");
    }

    [Fact(DisplayName = "Refill is capped at MaxTokens: a long idle period never exceeds capacity")]
    public async Task Refill_DoesNotExceedCapacity()
    {
        // High rate + long-ish wait would over-fill an uncapped bucket; capacity 2 must hold. After a generous wait we
        // should get exactly 2 allowed requests and then a denial, proving the Math.Min(maxTokens, ...) cap.
        using var limiter = NewLimiter(BaseOptions(2, 1000));

        limiter.AllowRequest("cap", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeTrue();
        limiter.AllowRequest("cap", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeTrue();
        limiter.AllowRequest("cap", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeFalse();

        await Task.Delay(100); // 1000 tokens/s * 0.1s == 100 tokens of refill, but capacity caps it at 2.

        limiter.AllowRequest("cap", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeTrue();
        limiter.AllowRequest("cap", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeTrue();
        limiter.AllowRequest("cap", typeof(RateLimiterAndResilienceInternalsTests))
            .Should().BeFalse("the refill must not have pushed the bucket above MaxTokens");
    }

    [Fact(DisplayName = "Concurrent acquire never grants more than MaxTokens permits")]
    public async Task ConcurrentAcquire_NeverOverGrants()
    {
        const int maxTokens = 8;
        const int contenders = maxTokens * 6;
        // Negligible refill rate so the only tokens available are the initial MaxTokens, regardless of timing jitter.
        using var limiter = NewLimiter(BaseOptions(maxTokens, 0.0001));

        var granted = 0;
        var start = new ManualResetEventSlim(false);
        var tasks = new Task[contenders];

        for (var i = 0; i < contenders; i++)
            tasks[i] = Task.Run(() =>
            {
                start.Wait();
                if (limiter.AllowRequest("race", typeof(RateLimiterAndResilienceInternalsTests)))
                    Interlocked.Increment(ref granted);
            });

        start.Set();
        await Task.WhenAll(tasks);

        granted.Should().Be(maxTokens, "the token bucket lock must hand out at most MaxTokens under contention");
    }

    [Fact(DisplayName = "PerCommand scope: same user is rate-limited independently per request type")]
    public void PerCommandScope_IndependentBucketsPerType()
    {
        using var limiter = NewLimiter(BaseOptions(1, 0.0001, RateLimitScope.PerCommand));

        // Drain the bucket for one request type.
        limiter.AllowRequest("user", typeof(PerCommandTypeA)).Should().BeTrue();
        limiter.AllowRequest("user", typeof(PerCommandTypeA)).Should().BeFalse("type A is now exhausted");

        // A different request type for the SAME user has its own bucket.
        limiter.AllowRequest("user", typeof(PerCommandTypeB)).Should().BeTrue("type B has an independent bucket in PerCommand scope");
    }

    [Fact(DisplayName = "Global scope: same user shares one bucket across request types")]
    public void GlobalScope_SharedBucketAcrossTypes()
    {
        using var limiter = NewLimiter(BaseOptions(1, 0.0001, RateLimitScope.Global));

        limiter.AllowRequest("user", typeof(PerCommandTypeA)).Should().BeTrue();
        limiter.AllowRequest("user", typeof(PerCommandTypeB))
            .Should().BeFalse("in Global scope the bucket is keyed by user only, so the type-B call hits the drained bucket");
    }

    [Theory(DisplayName = "AllowRequest rejects blank user identifiers on both overloads")]
    [InlineData("")]
    [InlineData("   ")]
    public void AllowRequest_BlankUser_Throws(string user)
    {
        using var limiter = NewLimiter(BaseOptions(1, 1));

        var stringOverload = () => limiter.AllowRequest(user, "command");
        var typeOverload = () => limiter.AllowRequest(user, typeof(RateLimiterAndResilienceInternalsTests));

        stringOverload.Should().Throw<ArgumentNullException>();
        typeOverload.Should().Throw<ArgumentNullException>();
    }

    [Theory(DisplayName = "AllowRequest(string,string) rejects a blank command name")]
    [InlineData("")]
    [InlineData("   ")]
    public void AllowRequest_BlankCommandName_Throws(string command)
    {
        using var limiter = NewLimiter(BaseOptions(1, 1));

        var act = () => limiter.AllowRequest("user", command);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact(DisplayName = "AllowRequest(string,Type) rejects a null request type")]
    public void AllowRequest_NullType_Throws()
    {
        using var limiter = NewLimiter(BaseOptions(1, 1));

        var act = () => limiter.AllowRequest("user", (Type)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory(DisplayName = "Constructor validates each configuration field independently")]
    [InlineData(0, 1, 1)] // MaxTokens <= 0
    [InlineData(-5, 1, 1)] // MaxTokens negative
    [InlineData(1, 0, 1)] // ReplenishRatePerSecond <= 0
    [InlineData(1, -2, 1)] // ReplenishRatePerSecond negative
    [InlineData(1, 1, 0)] // MaxEntries <= 0
    [InlineData(1, 1, -1)] // MaxEntries negative
    public void Constructor_InvalidConfiguration_Throws(int maxTokens, double rate, int maxEntries)
    {
        var options = BaseOptions(maxTokens, rate);
        options.MaxEntries = maxEntries;

        var act = () => NewLimiter(options);

        act.Should().Throw<ArgumentException>();
    }

    [Fact(DisplayName = "Valid default-shaped configuration constructs without throwing")]
    public void Constructor_ValidConfiguration_Succeeds()
    {
        var act = () => NewLimiter(BaseOptions(3, 1));

        act.Should().NotThrow();
    }

    [Fact(DisplayName = "Same key returns the same bucket so consumption is cumulative (LruCache reuse branch)")]
    public void SameKey_ReusesBucket_CumulativeConsumption()
    {
        // Exercises LruCache.GetOrAdd's existing-key path: repeated lookups for the same key must hit one bucket and
        // drain it cumulatively rather than minting a fresh full bucket each time.
        using var limiter = NewLimiter(BaseOptions(3, 0.0001));

        limiter.AllowRequest("same", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeTrue();
        limiter.AllowRequest("same", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeTrue();
        limiter.AllowRequest("same", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeTrue();
        limiter.AllowRequest("same", typeof(RateLimiterAndResilienceInternalsTests))
            .Should().BeFalse("the three calls shared one bucket and drained it");
    }

    [Fact(DisplayName = "Many distinct keys past MaxEntries keep serving without faulting (LruCache capacity-eviction branch)")]
    public void ManyKeys_TriggerCapacityEviction_WithoutFaulting()
    {
        // MaxEntries == 1 forces a per-shard capacity of 1 (capacity/shardCount + 1 == 1 for any shard count), so every
        // new key that lands in an occupied shard drives the LruCache's "_map.Count > capacity -> RemoveLast" eviction
        // path. We can't name the specific victim (the cache is sharded and internal), but we can prove the eviction
        // path runs for thousands of keys without throwing and that each fresh key gets its single token.
        var options = BaseOptions(1, 0.0001);
        options.MaxEntries = 1;
        using var limiter = NewLimiter(options);

        for (var i = 0; i < 3000; i++)
            limiter.AllowRequest($"key-{i}", typeof(RateLimiterAndResilienceInternalsTests))
                .Should().BeTrue("each brand-new key starts with a full single-token bucket");
    }

    [Fact(DisplayName = "Idle buckets are swept and re-created fresh on next access (LruCache RemoveWhere branch)")]
    public async Task IdleBucket_SweptAndRecreatedFresh()
    {
        // Opt into the per-request idle sweep: CleanupInterval == Zero disables the timer, MaxIdleTime > Zero enables
        // CleanupStaleBucketsIfNeeded. The internal cadence is floored at one second, so we drain the bucket, wait past
        // both the idle threshold and the cadence, then a fresh request both triggers the RemoveWhere sweep and re-adds
        // a full bucket -> the previously-exhausted key is allowed again.
        var options = BaseOptions(1, 0.0001);
        options.MaxIdleTime = TimeSpan.FromMilliseconds(1);
        options.CleanupInterval = TimeSpan.Zero;
        using var limiter = NewLimiter(options);

        limiter.AllowRequest("idle", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeTrue();
        limiter.AllowRequest("idle", typeof(RateLimiterAndResilienceInternalsTests)).Should().BeFalse("the single token is spent");

        // Exceed the 1-second cleanup cadence floor so the next call performs the idle sweep.
        await Task.Delay(1200);

        limiter.AllowRequest("idle", typeof(RateLimiterAndResilienceInternalsTests))
            .Should().BeTrue("the idle bucket was swept and a fresh full bucket was created on this access");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // ResilienceOptions.ComputeRetryDelay branch coverage
    // ---------------------------------------------------------------------------------------------------------------

    [Theory(DisplayName = "Non-positive retry attempt yields zero delay")]
    [InlineData(0)]
    [InlineData(-1)]
    public void ComputeRetryDelay_NonPositiveAttempt_ReturnsZero(int attempt)
    {
        var options = new ResilienceOptions { BaseDelay = TimeSpan.FromSeconds(5) };

        options.ComputeRetryDelay(attempt).Should().Be(TimeSpan.Zero);
    }

    [Fact(DisplayName = "Non-positive base delay disables delays entirely")]
    public void ComputeRetryDelay_ZeroBaseDelay_ReturnsZero()
    {
        var options = new ResilienceOptions { BaseDelay = TimeSpan.Zero };

        options.ComputeRetryDelay(1).Should().Be(TimeSpan.Zero);
        options.ComputeRetryDelay(3).Should().Be(TimeSpan.Zero);
    }

    [Fact(DisplayName = "Negative base delay is treated as delays-disabled")]
    public void ComputeRetryDelay_NegativeBaseDelay_ReturnsZero()
    {
        var options = new ResilienceOptions { BaseDelay = TimeSpan.FromSeconds(-1) };

        options.ComputeRetryDelay(2).Should().Be(TimeSpan.Zero);
    }

    [Theory(DisplayName = "Backoff multiplier <= 1 produces a fixed (non-growing) delay equal to BaseDelay")]
    [InlineData(1.0)]
    [InlineData(0.5)] // clamped up to 1.0
    [InlineData(-3.0)] // clamped up to 1.0
    public void ComputeRetryDelay_FixedDelay(double multiplier)
    {
        var options = new ResilienceOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(200),
            BackoffMultiplier = multiplier,
            MaxDelay = TimeSpan.FromMinutes(10)
        };

        options.ComputeRetryDelay(1).Should().Be(TimeSpan.FromMilliseconds(200));
        options.ComputeRetryDelay(4).Should().Be(TimeSpan.FromMilliseconds(200), "a clamped/unit multiplier keeps the delay flat across attempts");
    }

    [Theory(DisplayName = "NaN / infinite multipliers are clamped to a fixed BaseDelay")]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ComputeRetryDelay_NonFiniteMultiplier_FixedDelay(double multiplier)
    {
        var options = new ResilienceOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(250),
            BackoffMultiplier = multiplier,
            MaxDelay = TimeSpan.FromMinutes(10)
        };

        options.ComputeRetryDelay(3).Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact(DisplayName = "Exponential backoff grows by the multiplier per attempt")]
    public void ComputeRetryDelay_ExponentialGrowth()
    {
        var options = new ResilienceOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(100),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromMinutes(10)
        };

        options.ComputeRetryDelay(1).Should().Be(TimeSpan.FromMilliseconds(100)); // 100 * 2^0
        options.ComputeRetryDelay(2).Should().Be(TimeSpan.FromMilliseconds(200)); // 100 * 2^1
        options.ComputeRetryDelay(3).Should().Be(TimeSpan.FromMilliseconds(400)); // 100 * 2^2
        options.ComputeRetryDelay(4).Should().Be(TimeSpan.FromMilliseconds(800)); // 100 * 2^3
    }

    [Fact(DisplayName = "Backoff is capped at MaxDelay")]
    public void ComputeRetryDelay_CappedAtMaxDelay()
    {
        var options = new ResilienceOptions
        {
            BaseDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 10.0,
            MaxDelay = TimeSpan.FromSeconds(5)
        };

        // 1s * 10^2 == 100s, far past the 5s cap.
        options.ComputeRetryDelay(3).Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "A non-positive MaxDelay disables the cap so backoff grows unbounded")]
    public void ComputeRetryDelay_ZeroMaxDelay_NoCap()
    {
        var options = new ResilienceOptions
        {
            BaseDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 10.0,
            MaxDelay = TimeSpan.Zero
        };

        // With the cap disabled, 1s * 10^2 == 100s is returned in full.
        options.ComputeRetryDelay(3).Should().Be(TimeSpan.FromSeconds(100));
    }

    [Fact(DisplayName = "An overflowing (infinite) computed delay falls back to BaseDelay")]
    public void ComputeRetryDelay_OverflowFallsBackToBaseDelay()
    {
        // A huge multiplier with the cap disabled overflows Math.Pow to +Infinity; the guard returns BaseDelay.
        var options = new ResilienceOptions
        {
            BaseDelay = TimeSpan.FromSeconds(2),
            BackoffMultiplier = double.MaxValue,
            MaxDelay = TimeSpan.Zero
        };

        options.ComputeRetryDelay(5).Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact(DisplayName = "Default ResilienceOptions match the documented defaults")]
    public void Defaults_AreAsDocumented()
    {
        var options = new ResilienceOptions();

        options.MaxRetries.Should().Be(3);
        options.BaseDelay.Should().Be(TimeSpan.FromSeconds(1));
        options.BackoffMultiplier.Should().Be(1.0);
        options.MaxDelay.Should().Be(TimeSpan.FromSeconds(30));

        // Defaults imply a fixed 1s delay for every attempt (unit multiplier, well under the 30s cap).
        options.ComputeRetryDelay(1).Should().Be(TimeSpan.FromSeconds(1));
        options.ComputeRetryDelay(5).Should().Be(TimeSpan.FromSeconds(1));
    }

    // Private nested request-type markers used only to key PerCommand-scope buckets in these tests; uniquely named to
    // avoid collisions with shared fixtures.
    private sealed class PerCommandTypeA;

    private sealed class PerCommandTypeB;
}