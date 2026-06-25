using System.Runtime.CompilerServices;
using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Models.Requests;
using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Pipelines.Behaviors.RateLimiting.Context;
using CQRSharp.Pipelines.Behaviors.Resilience;
using CQRSharp.Pipelines.Behaviors.Timeout;
using CQRSharp.Pipelines.Options;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Behavior tests for the streaming pipeline behaviors over <see cref="IAsyncEnumerable{T}" />, mirroring the
///     non-stream variants but accounting for the streaming-specific policy. Covers:
///     <list type="bullet">
///         <item><see cref="StreamRateLimitingBehavior{TRequest,TItem}" /> — allows/blocks streams per the limiter.</item>
///         <item><see cref="StreamTimeoutBehavior{TRequest,TItem}" /> — cancels a stream that exceeds the budget,
///         passes one within it.</item>
///         <item><see cref="StreamResilienceBehavior{TRequest,TItem}" /> — retries only for an
///         <see cref="IRetryableRequest" /> that fails before yielding any items; rate-limit, timeout, cancellation,
///         post-yield faults, and non-retryable requests are all terminal.</item>
///     </list>
///     Timings are kept tiny and token-honoring to stay deterministic.
/// </summary>
public class StreamPipelineBehaviorTests
{
    // ---- Fixtures ---------------------------------------------------------

    /// <summary>A streaming request whose context is an <see cref="IRateLimitedContext" />.</summary>
    private sealed class StreamRateLimitedRequest : RequestBase<IRateLimitedContext>;

    /// <summary>A plain (non-retryable) streaming request.</summary>
    private sealed class StreamPlainRequest : IRequest
    {
        public IRequestContext? Context { get; set; }
        public RequestMetadata? Metadata { get; set; }
    }

    /// <summary>An opt-in retryable streaming request.</summary>
    private sealed class StreamRetryableRequest : IRetryableRequest
    {
        public IRequestContext? Context { get; set; }
        public RequestMetadata? Metadata { get; set; }
    }

    /// <summary>Yields the given items, honoring cancellation between each, with a tiny async hop.</summary>
    private static async IAsyncEnumerable<int> Produce(
        IEnumerable<int> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    /// <summary>Drains an async stream into a list.</summary>
    private static async Task<List<int>> Drain(IAsyncEnumerable<int> stream)
    {
        var results = new List<int>();
        await foreach (var item in stream.ConfigureAwait(false))
            results.Add(item);
        return results;
    }

    // ---- Rate limiting ----------------------------------------------------

    private static StreamRateLimitingBehavior<StreamRateLimitedRequest, int> CreateRateLimitingBehavior(
        RateLimiter limiter)
        => new(NullLogger<StreamRateLimitingBehavior<StreamRateLimitedRequest, int>>.Instance, limiter);

    private static RateLimiter CreateLimiter(int maxTokens)
        => new(Options.Create(new RateLimiterOptions
        {
            MaxTokens = maxTokens,
            ReplenishRatePerSecond = 1,
            Scope = RateLimitScope.PerCommand
        }));

    [Fact(DisplayName = "Stream rate limiting: a request under the limit is allowed and streams all items")]
    public async Task RateLimiting_UnderLimit_AllowsStream()
    {
        using var limiter = CreateLimiter(maxTokens: 3);
        var behavior = CreateRateLimitingBehavior(limiter);
        var request = new StreamRateLimitedRequest { Context = new TestRateLimitedContext("r1", "u1") };

        var items = await Drain(behavior.Handle(request, ct => Produce(new[] { 0, 1, 2 }, ct), CancellationToken.None));

        items.Should().Equal(0, 1, 2);
    }

    [Fact(DisplayName = "Stream rate limiting: once tokens are exhausted the stream is blocked with RateLimitExceededException")]
    public async Task RateLimiting_OverLimit_BlocksStream()
    {
        using var limiter = CreateLimiter(maxTokens: 2);
        var behavior = CreateRateLimitingBehavior(limiter);
        var ctx = new TestRateLimitedContext("r2", "u2");
        var request = new StreamRateLimitedRequest { Context = ctx };

        // Consume the two available tokens.
        for (var i = 0; i < 2; i++)
            await Drain(behavior.Handle(request, ct => Produce(new[] { i }, ct), CancellationToken.None));

        // The third stream must be throttled. The token is checked before enumeration begins; the
        // throttle surfaces when the consumer starts iterating.
        Func<Task> act = () => Drain(behavior.Handle(request, ct => Produce(new[] { 99 }, ct), CancellationToken.None));

        await act.Should().ThrowAsync<RateLimitExceededException>()
            .WithMessage($"*{ctx.RequestId}*{ctx.UserId}*rate limit*");
    }

    [Fact(DisplayName = "Stream rate limiting: a request without IRateLimitedContext is a no-op")]
    public async Task RateLimiting_NoRateLimitedContext_NoOp()
    {
        using var limiter = CreateLimiter(maxTokens: 1);
        var behavior = new StreamRateLimitingBehavior<StreamPlainRequest, int>(
            NullLogger<StreamRateLimitingBehavior<StreamPlainRequest, int>>.Instance, limiter);

        // No context => the limiter is never consulted, so far more than MaxTokens streams succeed.
        for (var i = 0; i < 5; i++)
        {
            var items = await Drain(behavior.Handle(new StreamPlainRequest(), ct => Produce(new[] { i }, ct), CancellationToken.None));
            items.Should().Equal(i);
        }
    }

    [Fact(DisplayName = "Stream rate limiting: missing user identifier throws InvalidOperationException")]
    public async Task RateLimiting_MissingUser_Throws()
    {
        using var limiter = CreateLimiter(maxTokens: 3);
        var behavior = CreateRateLimitingBehavior(limiter);
        var request = new StreamRateLimitedRequest { Context = new TestRateLimitedContext("r0", string.Empty) };

        Func<Task> act = () => Drain(behavior.Handle(request, ct => Produce(new[] { 0 }, ct), CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- Timeout ----------------------------------------------------------

    private static StreamTimeoutBehavior<StreamPlainRequest, int> CreateTimeoutBehavior(TimeSpan timeout)
        => new(
            NullLogger<StreamTimeoutBehavior<StreamPlainRequest, int>>.Instance,
            Options.Create(new TimeoutOptions { Timeout = timeout }));

    [Fact(DisplayName = "Stream timeout: a stream completing within the budget passes all items through")]
    public async Task Timeout_WithinBudget_PassesStreamThrough()
    {
        var behavior = CreateTimeoutBehavior(TimeSpan.FromSeconds(30));

        var items = await Drain(behavior.Handle(
            new StreamPlainRequest(),
            ct => Produce(new[] { 0, 1, 2 }, ct),
            CancellationToken.None));

        items.Should().Equal(0, 1, 2);
    }

    [Fact(DisplayName = "Stream timeout: a stream exceeding the budget is cancelled and surfaced as TimeoutException")]
    public async Task Timeout_ExceedingBudget_ThrowsTimeoutException()
    {
        // Very short budget; the producer yields one item then stalls far longer than the timeout, honoring
        // the combined token, so the timeout source fires first.
        var behavior = CreateTimeoutBehavior(TimeSpan.FromMilliseconds(20));

        async IAsyncEnumerable<int> Slow([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 0;
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            yield return 1;
        }

        Func<Task> act = () => Drain(behavior.Handle(new StreamPlainRequest(), Slow, CancellationToken.None));

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact(DisplayName = "Stream timeout: caller cancellation surfaces as OperationCanceledException, not TimeoutException")]
    public async Task Timeout_CallerCancellation_IsNotMappedToTimeout()
    {
        // Generous timeout so only the caller's cancellation fires.
        var behavior = CreateTimeoutBehavior(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        async IAsyncEnumerable<int> Slow([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 0;
            // ReSharper disable once AccessToDisposedClosure
            cts.Cancel();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            yield return 1;
        }

        Func<Task> act = () => Drain(behavior.Handle(new StreamPlainRequest(), Slow, cts.Token));

        // The catch filter only maps to TimeoutException when the timeout source fired; caller cancellation
        // must propagate as a plain OperationCanceledException.
        var assertion = await act.Should().ThrowAsync<OperationCanceledException>();
        assertion.Which.Should().NotBeOfType<TimeoutException>();
    }

    // ---- Resilience -------------------------------------------------------

    private static StreamResilienceBehavior<TRequest, int> CreateResilienceBehavior<TRequest>(int maxRetries = 3)
        where TRequest : IRequest
        => new(
            NullLogger<StreamResilienceBehavior<TRequest, int>>.Instance,
            Options.Create(new ResilienceOptions { MaxRetries = maxRetries, BaseDelay = TimeSpan.Zero }));

    [Fact(DisplayName = "Stream resilience: a successful stream passes its items through unchanged")]
    public async Task Resilience_Success_PassesThrough()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>();

        var items = await Drain(behavior.Handle(
            new StreamRetryableRequest(),
            ct => Produce(new[] { 0, 1, 2 }, ct),
            CancellationToken.None));

        items.Should().Equal(0, 1, 2);
    }

    [Fact(DisplayName = "Stream resilience: a retryable stream failing before any item is retried until it succeeds")]
    public async Task Resilience_Retryable_RetriesUntilSuccess_WhenFailsBeforeYield()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>(maxRetries: 3);
        var attempts = 0;

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            attempts++;
            // Fail before yielding anything on the first two attempts.
            if (attempts < 3)
            {
                await Task.Yield();
                throw new InvalidOperationException("transient");
            }

            await foreach (var item in Produce(new[] { 0, 1 }, ct).ConfigureAwait(false))
                yield return item;
        }

        var items = await Drain(behavior.Handle(new StreamRetryableRequest(), Source, CancellationToken.None));

        items.Should().Equal(0, 1);
        attempts.Should().Be(3, "the stream failed before yielding on the first two attempts and is retried");
    }

    [Fact(DisplayName = "Stream resilience: a non-retryable stream failure propagates without retrying")]
    public async Task Resilience_NonRetryable_DoesNotRetry()
    {
        var behavior = CreateResilienceBehavior<StreamPlainRequest>();
        var attempts = 0;

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            attempts++;
            await Task.Yield();
            throw new InvalidOperationException("boom");
#pragma warning disable CS0162 // Unreachable code — required so the method is an iterator.
            yield break;
#pragma warning restore CS0162
        }

        Func<Task> act = () => Drain(behavior.Handle(new StreamPlainRequest(), Source, CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        attempts.Should().Be(1, "a non-IRetryableRequest stream must not be retried");
    }

    [Fact(DisplayName = "Stream resilience: a retryable stream that faults AFTER yielding items is not retried (avoids duplicates)")]
    public async Task Resilience_Retryable_FaultAfterYield_IsNotRetried()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>();
        var attempts = 0;
        var seen = new List<int>();

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            attempts++;
            yield return 0;
            await Task.Yield();
            throw new InvalidOperationException("post-yield");
        }

        Func<Task> act = async () =>
        {
            await foreach (var item in behavior.Handle(new StreamRetryableRequest(), Source, CancellationToken.None).ConfigureAwait(false))
                seen.Add(item);
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("post-yield");
        attempts.Should().Be(1, "retries are disabled once items have been yielded to avoid duplicate partial results");
        seen.Should().Equal(new[] { 0 }, "the item yielded before the fault is still observed by the consumer");
    }

    [Fact(DisplayName = "Stream resilience: caller cancellation is never retried, even for a retryable request")]
    public async Task Resilience_Cancellation_IsNotRetried()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>();
        var attempts = 0;

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            attempts++;
            await Task.Yield();
            throw new OperationCanceledException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        Func<Task> act = () => Drain(behavior.Handle(new StreamRetryableRequest(), Source, CancellationToken.None));

        await act.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(1, "cancellation is terminal and must not be retried");
    }

    [Fact(DisplayName = "Stream resilience: a TimeoutException is terminal and never retried")]
    public async Task Resilience_Timeout_IsNotRetried()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>();
        var attempts = 0;

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            attempts++;
            await Task.Yield();
            throw new TimeoutException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        Func<Task> act = () => Drain(behavior.Handle(new StreamRetryableRequest(), Source, CancellationToken.None));

        await act.Should().ThrowAsync<TimeoutException>();
        attempts.Should().Be(1, "retrying a timeout would multiply the configured time budget");
    }

    [Fact(DisplayName = "Stream resilience: a RateLimitExceededException is terminal and never retried")]
    public async Task Resilience_RateLimit_IsNotRetried()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>();
        var attempts = 0;
        var ctx = new TestRateLimitedContext("rl", "u-rl");

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            attempts++;
            await Task.Yield();
            throw new RateLimitExceededException(ctx, "Rate limit exceeded for user.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        Func<Task> act = () => Drain(behavior.Handle(new StreamRetryableRequest(), Source, CancellationToken.None));

        await act.Should().ThrowAsync<RateLimitExceededException>();
        attempts.Should().Be(1, "an upstream rate-limit rejection is terminal and must not be retried");
    }

    [Fact(DisplayName = "Stream resilience: a retryable stream still failing before yield after all retries propagates the failure")]
    public async Task Resilience_Retryable_RetriesExhausted_Throws()
    {
        var behavior = CreateResilienceBehavior<StreamRetryableRequest>(maxRetries: 2);
        var attempts = 0;

        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            attempts++;
            await Task.Yield();
            throw new InvalidOperationException("always");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        Func<Task> act = () => Drain(behavior.Handle(new StreamRetryableRequest(), Source, CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("always");
        // 1 initial attempt + MaxRetries(2) retries = 3 total enumerations.
        attempts.Should().Be(3, "the initial attempt plus MaxRetries retries are all consumed before propagating");
    }
}
