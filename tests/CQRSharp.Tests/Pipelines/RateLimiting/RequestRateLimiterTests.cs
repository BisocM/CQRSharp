using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     The <see cref="RequestRateLimiter" /> on its own: token-bucket boundaries, the retry-after it reports, concurrent
///     acquisition, scopes, and how it keeps its bucket count bounded without handing a throttled caller a fresh bucket.
///     Every limiter runs on a <see cref="FakeTimeProvider" />, so time only moves when a test moves it.
/// </summary>
public class RequestRateLimiterTests
{
    private readonly FakeTimeProvider _time = new();

    private static RateLimitingOptions BaseOptions(int maxTokens, double rate, RateLimitScope scope = RateLimitScope.Global)
        => new()
        {
            MaxTokens = maxTokens,
            ReplenishRatePerSecond = rate,
            Scope = scope,
            MaxEntries = 64,
            // No idle sweep unless a test opts in.
            CleanupInterval = TimeSpan.Zero,
            MaxIdleTime = TimeSpan.Zero
        };

    private RequestRateLimiter NewLimiter(RateLimitingOptions options) => new(Options.Create(options), _time);

    private static bool Acquire(RequestRateLimiter limiter, string user, Type? requestType = null)
        => limiter.TryAcquire(user, requestType ?? typeof(RequestRateLimiterTests), out _);

    [Fact(DisplayName = "Burst boundary: a full bucket allows exactly MaxTokens requests then blocks")]
    public void Burst_AllowsExactlyMaxTokens_ThenBlocks()
    {
        using var limiter = NewLimiter(BaseOptions(5, 1));

        for (var i = 0; i < 5; i++)
            Acquire(limiter, "burst-user").Should().BeTrue($"token #{i + 1} of the initial burst should be available");

        Acquire(limiter, "burst-user").Should().BeFalse("the bucket is empty once all MaxTokens have been consumed");
    }

    [Fact(DisplayName = "Tokens accrue continuously: a partial token grants nothing, and the whole token arrives mid-second")]
    public void Refill_is_continuous_and_grants_only_whole_tokens()
    {
        // Two tokens a second: one every 500ms.
        using var limiter = NewLimiter(BaseOptions(1, 2));

        Acquire(limiter, "frac").Should().BeTrue("the bucket starts full");
        Acquire(limiter, "frac").Should().BeFalse("the only token was just consumed");

        _time.Advance(TimeSpan.FromMilliseconds(400));

        limiter.TryAcquire("frac", typeof(RequestRateLimiterTests), out var retryAfter)
            .Should().BeFalse("0.8 of a token has accrued, and a partial token grants nothing");
        retryAfter.Should().Be(TimeSpan.FromMilliseconds(100));

        _time.Advance(TimeSpan.FromMilliseconds(200));

        Acquire(limiter, "frac").Should().BeTrue("a whole token accrued within the second, not at its end");
    }

    [Fact(DisplayName = "Refill is capped at MaxTokens: a long idle period never exceeds capacity")]
    public void Refill_DoesNotExceedCapacity()
    {
        using var limiter = NewLimiter(BaseOptions(2, 1000));

        Acquire(limiter, "cap").Should().BeTrue();
        Acquire(limiter, "cap").Should().BeTrue();
        Acquire(limiter, "cap").Should().BeFalse();

        _time.Advance(TimeSpan.FromMilliseconds(100)); // 100 tokens of refill, capped at 2.

        Acquire(limiter, "cap").Should().BeTrue();
        Acquire(limiter, "cap").Should().BeTrue();
        Acquire(limiter, "cap").Should().BeFalse("the refill must not have pushed the bucket above MaxTokens");
    }

    [Fact(DisplayName = "A rejection reports the time until the next token, and it shrinks as time passes")]
    public void Rejection_reports_the_time_until_the_next_token()
    {
        using var limiter = NewLimiter(BaseOptions(1, 0.5));

        limiter.TryAcquire("u", typeof(RequestRateLimiterTests), out var granted).Should().BeTrue();
        granted.Should().Be(TimeSpan.Zero);

        limiter.TryAcquire("u", typeof(RequestRateLimiterTests), out var retryAfter).Should().BeFalse();
        retryAfter.Should().Be(TimeSpan.FromSeconds(2), "at half a token per second a whole token takes two seconds");

        _time.Advance(TimeSpan.FromMilliseconds(500));

        limiter.TryAcquire("u", typeof(RequestRateLimiterTests), out retryAfter).Should().BeFalse();
        retryAfter.Should().Be(TimeSpan.FromSeconds(1.5));
    }

    // Timers count whole milliseconds and Task.Delay drops the fraction: a caller told the exact 999.93 ms would wait
    // 999 ms and be rejected again.
    [Theory(DisplayName = "RetryAfter is whole milliseconds, rounded up, so a caller that waits it out with a timer is never back early")]
    [InlineData(1.0, 700, 1000)] // 0.07 ms of refill: the exact wait is 999.93 ms
    [InlineData(3.0, 0, 334)] // a token every 333.33 ms
    public void Retry_after_never_understates_the_wait(double rate, long refillTicks, int expectedMilliseconds)
    {
        using var limiter = NewLimiter(BaseOptions(1, rate));
        Acquire(limiter, "u").Should().BeTrue();
        _time.Advance(TimeSpan.FromTicks(refillTicks));

        limiter.TryAcquire("u", typeof(RequestRateLimiterTests), out var retryAfter).Should().BeFalse();
        retryAfter.Should().Be(TimeSpan.FromMilliseconds(expectedMilliseconds));

        _time.Advance(retryAfter - TimeSpan.FromMilliseconds(1));
        Acquire(limiter, "u").Should().BeFalse("a millisecond short of RetryAfter the token is not there yet");

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Acquire(limiter, "u").Should().BeTrue("waiting exactly RetryAfter is enough");
    }

    [Fact(DisplayName = "A refill rate too slow for TimeSpan reports TimeSpan.MaxValue instead of overflowing")]
    public void Tiny_rate_clamps_retry_after()
    {
        using var limiter = NewLimiter(BaseOptions(1, 1e-12));

        Acquire(limiter, "slow").Should().BeTrue();

        limiter.TryAcquire("slow", typeof(RequestRateLimiterTests), out var retryAfter).Should().BeFalse();
        retryAfter.Should().Be(TimeSpan.MaxValue);
    }

    [Fact(DisplayName = "Concurrent acquire never grants more than MaxTokens permits")]
    public async Task ConcurrentAcquire_NeverOverGrants()
    {
        const int maxTokens = 8;
        const int contenders = maxTokens * 6;
        using var limiter = NewLimiter(BaseOptions(maxTokens, 1));

        var granted = 0;
        using var start = new ManualResetEventSlim(false);
        var tasks = new Task[contenders];

        for (var i = 0; i < contenders; i++)
            tasks[i] = Task.Run(() =>
            {
                start.Wait();
                if (Acquire(limiter, "race"))
                    Interlocked.Increment(ref granted);
            }, TestContext.Current.CancellationToken);

        start.Set();
        await Task.WhenAll(tasks);

        granted.Should().Be(maxTokens, "the token bucket lock must hand out at most MaxTokens under contention");
    }

    [Fact(DisplayName = "PerRequestType scope: the same user is limited independently per request type")]
    public void PerRequestTypeScope_IndependentBucketsPerType()
    {
        using var limiter = NewLimiter(BaseOptions(1, 1, RateLimitScope.PerRequestType));

        Acquire(limiter, "user", typeof(RequestTypeA)).Should().BeTrue();
        Acquire(limiter, "user", typeof(RequestTypeA)).Should().BeFalse("type A is now exhausted");

        Acquire(limiter, "user", typeof(RequestTypeB)).Should().BeTrue("type B has an independent bucket");
    }

    [Fact(DisplayName = "Global scope: the same user shares one bucket across request types")]
    public void GlobalScope_SharedBucketAcrossTypes()
    {
        using var limiter = NewLimiter(BaseOptions(1, 1));

        Acquire(limiter, "user", typeof(RequestTypeA)).Should().BeTrue();
        Acquire(limiter, "user", typeof(RequestTypeB))
            .Should().BeFalse("in Global scope the bucket is keyed by user only, so the type-B call hits the drained bucket");
    }

    [Theory(DisplayName = "TryAcquire rejects a blank user identifier")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryAcquire_BlankUser_Throws(string user)
    {
        using var limiter = NewLimiter(BaseOptions(1, 1));

        var act = () => limiter.TryAcquire(user, typeof(RequestRateLimiterTests), out _);

        act.Should().Throw<ArgumentException>();
    }

    [Fact(DisplayName = "TryAcquire rejects a null request type")]
    public void TryAcquire_NullType_Throws()
    {
        using var limiter = NewLimiter(BaseOptions(1, 1));

        var act = () => limiter.TryAcquire("user", null!, out _);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory(DisplayName = "Constructor validates each configuration field independently")]
    [InlineData(0, 1, 1)]
    [InlineData(-5, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, -2, 1)]
    [InlineData(1, double.NaN, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, -1)]
    public void Constructor_InvalidConfiguration_Throws(int maxTokens, double rate, int maxEntries)
    {
        var options = BaseOptions(maxTokens, rate);
        options.MaxEntries = maxEntries;

        var act = () => NewLimiter(options);

        act.Should().Throw<ArgumentException>();
    }

    [Fact(DisplayName = "Same key returns the same bucket so consumption is cumulative")]
    public void SameKey_ReusesBucket_CumulativeConsumption()
    {
        using var limiter = NewLimiter(BaseOptions(3, 1));

        Acquire(limiter, "same").Should().BeTrue();
        Acquire(limiter, "same").Should().BeTrue();
        Acquire(limiter, "same").Should().BeTrue();
        Acquire(limiter, "same").Should().BeFalse("the three calls shared one bucket and drained it");
    }

    [Theory(DisplayName = "Up to MaxEntries drained callers all stay throttled: nothing is dropped before MaxEntries buckets exist")]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(200)]
    public void MaxEntries_drained_callers_all_stay_throttled(int maxEntries)
    {
        var options = BaseOptions(1, 1);
        options.MaxEntries = maxEntries;
        using var limiter = NewLimiter(options);

        for (var i = 0; i < maxEntries; i++)
            Acquire(limiter, $"key-{i}").Should().BeTrue();

        // However the keys hash, none has been dropped and handed a fresh bucket.
        for (var round = 0; round < 3; round++)
        for (var i = 0; i < maxEntries; i++)
            Acquire(limiter, $"key-{i}").Should().BeFalse($"key-{i} spent its only token and must stay throttled");
    }

    [Fact(DisplayName = "At MaxEntries, buckets that have refilled are dropped first, so no throttled caller loses its state")]
    public void Refilled_buckets_are_reclaimed_before_drained_ones()
    {
        var options = BaseOptions(1, 1);
        options.MaxEntries = 10;
        using var limiter = NewLimiter(options);

        // Five callers spend their token, then refill completely.
        for (var i = 0; i < 5; i++) Acquire(limiter, $"idle-{i}").Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(2));

        // Five more spend theirs and stay drained: the limiter is full.
        for (var i = 0; i < 5; i++) Acquire(limiter, $"busy-{i}").Should().BeTrue();

        // A new caller needs room: every refilled bucket goes, and nothing else.
        Acquire(limiter, "newcomer").Should().BeTrue();
        limiter.BucketCount.Should().Be(6, "the five refilled buckets were dropped, the five drained ones kept");

        for (var i = 0; i < 5; i++)
            Acquire(limiter, $"busy-{i}").Should().BeFalse($"busy-{i} is still throttled");
    }

    [Fact(DisplayName = "A flood of new callers never grows the limiter past MaxEntries, and pushes out the least recently used caller")]
    public void Flood_of_new_callers_stays_within_MaxEntries()
    {
        // A token a day: no bucket refills during the test, so only the least-recently-used pass can make room.
        var options = BaseOptions(1, 1.0 / 86400);
        options.MaxEntries = 100;
        using var limiter = NewLimiter(options);

        Acquire(limiter, "victim").Should().BeTrue();
        Acquire(limiter, "victim").Should().BeFalse("the victim spent its only token");

        for (var i = 0; i < 1_000; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(1));
            Acquire(limiter, $"flood-{i}").Should().BeTrue();
            limiter.BucketCount.Should().BeLessThanOrEqualTo(options.MaxEntries);
        }

        Acquire(limiter, "victim").Should().BeTrue("the victim was the least recently used caller, so its bucket was dropped");
    }

    [Fact(DisplayName = "At MaxEntries with every bucket in use, the least recently used bucket is the one dropped")]
    public void When_every_bucket_is_in_use_the_least_recently_used_is_dropped()
    {
        var options = BaseOptions(1, 1e-6);
        options.MaxEntries = 10;
        using var limiter = NewLimiter(options);

        for (var i = 0; i < 10; i++)
        {
            Acquire(limiter, $"key-{i}").Should().BeTrue();
            _time.Advance(TimeSpan.FromMilliseconds(1));
        }

        Acquire(limiter, "key-10").Should().BeTrue();
        limiter.BucketCount.Should().Be(10);

        // key-0 was the least recently used. Coming back takes the place of the next one, key-1, which is why the
        // assertions below start at key-2.
        Acquire(limiter, "key-0").Should().BeTrue("key-0 was dropped to make room and starts again with a full bucket");
        for (var i = 2; i <= 10; i++)
            Acquire(limiter, $"key-{i}").Should().BeFalse($"key-{i} was kept and is still throttled");
    }

    [Fact(DisplayName = "The idle sweep drops a bucket that has been idle for MaxIdleTime and has refilled")]
    public void Idle_refilled_bucket_is_swept()
    {
        var options = BaseOptions(1, 1);
        options.MaxIdleTime = TimeSpan.FromMilliseconds(1);
        using var limiter = NewLimiter(options);

        Acquire(limiter, "idle").Should().BeTrue();
        limiter.BucketCount.Should().Be(1);

        // Past the one-second sweep cadence floor and the refill time; another caller's request runs the sweep.
        _time.Advance(TimeSpan.FromMilliseconds(1200));
        Acquire(limiter, "other").Should().BeTrue();

        limiter.BucketCount.Should().Be(1, "the refilled, idle bucket was dropped and only the new caller's remains");
        Acquire(limiter, "idle").Should().BeTrue();
    }

    [Fact(DisplayName = "The idle sweep never drops a bucket that has not refilled, however long it has been idle")]
    public void Idle_sweep_keeps_a_bucket_that_is_still_short_of_tokens()
    {
        // One token a minute, an idle limit of a second: a sweep that dropped idle buckets regardless would let the
        // caller back in after a second instead of a minute.
        var options = BaseOptions(1, 1.0 / 60);
        options.MaxIdleTime = TimeSpan.FromSeconds(1);
        using var limiter = NewLimiter(options);

        Acquire(limiter, "slow").Should().BeTrue();

        _time.Advance(TimeSpan.FromSeconds(30));
        Acquire(limiter, "other").Should().BeTrue("a request from someone else runs the sweep");

        limiter.BucketCount.Should().Be(2, "the idle but half-empty bucket was kept");
        limiter.TryAcquire("slow", typeof(RequestRateLimiterTests), out var retryAfter)
            .Should().BeFalse("half a minute into a one-minute refill the caller is still throttled");
        retryAfter.Should().BeCloseTo(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1));
    }

    private sealed class RequestTypeA;

    private sealed class RequestTypeB;
}
