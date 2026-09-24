using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static CQRSharp.Tests.Pipelines.StreamBehaviorFixtures;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Behavior tests for <see cref="StreamRateLimitingBehavior{TRequest,TItem}" />: it takes the caller's token when
///     enumeration starts, rejects the stream when there is none, and is a no-op for a request without an
///     <see cref="IRateLimitedContext" />. The limiter runs on a <see cref="FakeTimeProvider" />, so no token refills
///     between the calls of a test.
/// </summary>
public class StreamRateLimitingBehaviorTests
{
    private static StreamRateLimitingBehavior<TRequest, int> CreateRateLimitingBehavior<TRequest>(RequestRateLimiter limiter)
        where TRequest : IRequest
        => new(NullLogger<StreamRateLimitingBehavior<TRequest, int>>.Instance, limiter);

    private static RequestRateLimiter CreateLimiter(int maxTokens)
        => new(Options.Create(new RateLimitingOptions
        {
            MaxTokens = maxTokens,
            ReplenishRatePerSecond = 1,
            Scope = RateLimitScope.PerRequestType
        }), new FakeTimeProvider());

    [Fact(DisplayName = "Stream rate limiting: a request under the limit is allowed and streams all items; the token is taken when enumeration starts")]
    public async Task RateLimiting_UnderLimit_AllowsStream()
    {
        using var limiter = CreateLimiter(3);
        var behavior = CreateRateLimitingBehavior<StreamRateLimitedRequest>(limiter);
        var request = new StreamRateLimitedRequest().WithContext(new TestRateLimitedContext("u1"));

        var stream = behavior.Handle(request, ct => Produce([0, 1, 2], ct), CancellationToken.None);
        limiter.BucketCount.Should().Be(0, "nothing is checked until the stream is enumerated");

        var items = await Drain(stream);

        items.Should().Equal(0, 1, 2);
        limiter.BucketCount.Should().Be(1);
    }

    [Fact(DisplayName = "Stream rate limiting: once tokens are exhausted the stream is blocked with RateLimitExceededException")]
    public async Task RateLimiting_OverLimit_BlocksStream()
    {
        using var limiter = CreateLimiter(2);
        var behavior = CreateRateLimitingBehavior<StreamRateLimitedRequest>(limiter);
        var request = new StreamRateLimitedRequest().WithContext(new TestRateLimitedContext("u2"));
        var sourceRequested = false;

        for (var i = 0; i < 2; i++)
            await Drain(behavior.Handle(request, ct => Produce([i], ct), CancellationToken.None));

        Func<Task> act = () => Drain(behavior.Handle(
            request,
            ct =>
            {
                sourceRequested = true;
                return Produce([99], ct);
            },
            CancellationToken.None));

        (await act.Should().ThrowExactlyAsync<RateLimitExceededException>()).Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(1));
        sourceRequested.Should().BeFalse("a throttled stream is rejected before its source is requested");
    }

    [Fact(DisplayName = "Stream rate limiting: a request without IRateLimitedContext is a no-op")]
    public async Task RateLimiting_NoRateLimitedContext_NoOp()
    {
        using var limiter = CreateLimiter(1);
        var behavior = CreateRateLimitingBehavior<StreamPlainRequest>(limiter);

        for (var i = 0; i < 5; i++)
        {
            var items = await Drain(behavior.Handle(new StreamPlainRequest(), ct => Produce([i], ct), CancellationToken.None));
            items.Should().Equal(i);
        }

        limiter.BucketCount.Should().Be(0, "the limiter was never consulted");
    }

    [Fact(DisplayName = "Stream rate limiting: missing user identifier throws InvalidOperationException")]
    public async Task RateLimiting_MissingUser_Throws()
    {
        using var limiter = CreateLimiter(3);
        var behavior = CreateRateLimitingBehavior<StreamRateLimitedRequest>(limiter);
        var request = new StreamRateLimitedRequest().WithContext(new TestRateLimitedContext(string.Empty));

        Func<Task> act = () => Drain(behavior.Handle(request, ct => Produce([0], ct), CancellationToken.None));

        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    /// <summary>A streaming request whose context is an <see cref="IRateLimitedContext" />.</summary>
    private sealed class StreamRateLimitedRequest : RequestBase<IRateLimitedContext>;
}
