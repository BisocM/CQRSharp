using System.Diagnostics;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     What <see cref="RateLimitingBehavior{TRequest,TResult}" /> adds to the <see cref="RequestRateLimiter" /> (whose
///     buckets, scopes and bounds <see cref="RequestRateLimiterTests" /> covers): which requests it checks, the key it
///     checks them under, how a rejection surfaces and is logged, its span, and the options validation of its
///     registration. The limiter runs on a <see cref="FakeTimeProvider" />, so no token refills unless a test says so.
/// </summary>
public sealed class RateLimitingBehaviorTests : IDisposable
{
    private const int MaxTokens = 3;

    private readonly FakeTimeProvider _time = new();
    private readonly RequestRateLimiter _limiter;

    public RateLimitingBehaviorTests()
        => _limiter = new RequestRateLimiter(Options.Create(new RateLimitingOptions
        {
            MaxTokens = MaxTokens,
            ReplenishRatePerSecond = 1,
            Scope = RateLimitScope.PerRequestType
        }), _time);

    public void Dispose() => _limiter.Dispose();

    private RateLimitingBehavior<TRequest, object> CreateBehavior<TRequest>(ILogger<RateLimitingBehavior<TRequest, object>>? logger = null)
        where TRequest : IRequest
        => new(logger ?? NullLogger<RateLimitingBehavior<TRequest, object>>.Instance, _limiter);

    private static Task<object> Next(CancellationToken cancellationToken) => Task.FromResult<object>("handled");

    [Fact(DisplayName = "Under the limit the request runs and its result passes through")]
    public async Task UnderLimit_RunsNext_AndReturnsItsResult()
    {
        var behavior = CreateBehavior<TestRateLimitedCommand>();

        var result = await behavior.Handle(new TestRateLimitedCommand().WithContext(new TestRateLimitedContext("u1")), Next, CancellationToken.None);

        result.Should().Be("handled");
    }

    [Fact(DisplayName = "Over the limit the request fails with RateLimitExceededException carrying the wait, and is logged once at Information")]
    public async Task OverLimit_Rejects_WithRetryAfter()
    {
        var logger = new CapturingLogger<RateLimitingBehavior<TestRateLimitedCommand, object>>();
        var behavior = CreateBehavior(logger);
        var request = new TestRateLimitedCommand().WithContext(new TestRateLimitedContext("u2"));
        for (var i = 0; i < MaxTokens; i++) await behavior.Handle(request, Next, CancellationToken.None);
        var nextCalled = false;

        var handling = behavior.Handle(
            request,
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<object>("never");
            },
            CancellationToken.None);

        handling.IsFaulted.Should().BeTrue("a rejection surfaces as a faulted task, like every other pipeline failure");
        var exception = (await FluentActions.Awaiting(() => handling).Should().ThrowExactlyAsync<RateLimitExceededException>()).Which;
        exception.RetryAfter.Should().Be(TimeSpan.FromSeconds(1), "the bucket is empty and refills one token a second");
        exception.Message.Should().Contain(nameof(TestRateLimitedCommand)).And.NotContain("u2", "the message names no caller");
        nextCalled.Should().BeFalse();

        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4500 && e.Level == LogLevel.Information && e.Exception == null &&
            e.Message.Contains("u2") && e.Message.Contains("1000ms"));
    }

    [Fact(DisplayName = "The behavior keys a request by its runtime type, not by TRequest or the type's name")]
    public async Task PerRequestTypeScope_KeysByRuntimeType()
    {
        // One behavior closed over the shared base type, and two request types with the same simple name: only
        // request.GetType() tells them apart.
        var behavior = CreateBehavior<RequestBase<IRateLimitedContext>>();
        var first = new Shared.CollisionsA.CollisionCommand().WithContext(new TestRateLimitedContext("u4"));
        var second = new Shared.CollisionsB.CollisionCommand().WithContext(new TestRateLimitedContext("u4"));
        for (var i = 0; i < MaxTokens; i++) await behavior.Handle(first, Next, CancellationToken.None);

        await FluentActions.Awaiting(() => behavior.Handle(first, Next, CancellationToken.None))
            .Should().ThrowAsync<RateLimitExceededException>("the first type's bucket is empty");
        await FluentActions.Awaiting(() => behavior.Handle(second, Next, CancellationToken.None))
            .Should().NotThrowAsync("the second type has a bucket of its own");
    }

    [Fact(DisplayName = "The behavior keys a request by the user id its context carries")]
    public async Task DifferentUsers_IndependentBuckets()
    {
        var behavior = CreateBehavior<TestRateLimitedCommand>();
        var exhausted = new TestRateLimitedCommand().WithContext(new TestRateLimitedContext("u6"));
        for (var i = 0; i < MaxTokens; i++) await behavior.Handle(exhausted, Next, CancellationToken.None);

        await FluentActions.Awaiting(() => behavior.Handle(exhausted, Next, CancellationToken.None))
            .Should().ThrowAsync<RateLimitExceededException>();
        await FluentActions.Awaiting(() => behavior.Handle(new TestRateLimitedCommand().WithContext(new TestRateLimitedContext("u7")), Next, CancellationToken.None))
            .Should().NotThrowAsync("another user has a bucket of their own");
    }

    [Fact(DisplayName = "A request whose context is not an IRateLimitedContext passes through without touching the limiter")]
    public async Task NoRateLimitedContext_NoOp()
    {
        var behavior = CreateBehavior<TestCommand>();

        for (var i = 0; i < MaxTokens + 1; i++)
            (await behavior.Handle(new TestCommand(), Next, CancellationToken.None)).Should().Be("handled");

        _limiter.BucketCount.Should().Be(0, "the limiter was never consulted");
    }

    [Theory(DisplayName = "A rate-limited context without a user id fails with an actionable InvalidOperationException")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingUser_Throws(string userId)
    {
        var behavior = CreateBehavior<TestRateLimitedCommand>();

        var exception = (await FluentActions.Awaiting(() => behavior.Handle(
                new TestRateLimitedCommand().WithContext(new TestRateLimitedContext(userId)), Next, CancellationToken.None))
            .Should().ThrowExactlyAsync<InvalidOperationException>()).Which;

        exception.Message.Should().Contain(nameof(TestRateLimitedCommand)).And.Contain("UserId");
        _limiter.BucketCount.Should().Be(0);
    }

    [Fact(DisplayName = "DI registration: invalid options fail options validation on resolution")]
    public void AddRateLimiting_InvalidOptions_FailsValidationOnResolution()
    {
        var services = new ServiceCollection();
        services.AddRateLimiting(options => options.MaxEntries = 0);

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<RequestRateLimiter>();
        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*MaxEntries must be greater than zero*");
    }

    [Fact(DisplayName = "Tracing: the RateLimiting.Check span covers the check only; it has ended before the rest of the pipeline runs")]
    public async Task Check_span_ends_before_next_runs()
    {
        using var spans = new PipelineSpanRecorder(typeof(SpanProbeCommand));
        var behavior = CreateBehavior<SpanProbeCommand>();
        string? currentInsideNext = null;
        var checkEndedBeforeNext = false;

        await behavior.Handle(
            new SpanProbeCommand().WithContext(new TestRateLimitedContext("span-user")),
            _ =>
            {
                currentInsideNext = Activity.Current?.OperationName;
                checkEndedBeforeNext = spans.Stopped.Any(a => a.OperationName == "RateLimiting.Check");
                return Task.FromResult<object>("ok");
            },
            CancellationToken.None);

        checkEndedBeforeNext.Should().BeTrue("the check's span is stopped before the handler runs");
        currentInsideNext.Should().NotBe("RateLimiting.Check", "the handler's spans must not become children of the check");
        spans.Stopped.Should().ContainSingle(a => a.OperationName == "RateLimiting.Check")
            .Which.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact(DisplayName = "Tracing: a request whose context does not opt in starts no RateLimiting.Check span")]
    public async Task No_span_for_a_request_that_does_not_opt_in()
    {
        using var spans = new PipelineSpanRecorder(typeof(PlainSpanProbeCommand));
        var behavior = CreateBehavior<PlainSpanProbeCommand>();

        await behavior.Handle(new PlainSpanProbeCommand(), _ => Task.FromResult<object>("ok"), CancellationToken.None);

        spans.Stopped.Should().BeEmpty();
    }

    [Fact(DisplayName = "Tracing: a throttled request's check span is an error carrying the retry-after")]
    public async Task Throttled_span_is_an_error()
    {
        using var spans = new PipelineSpanRecorder(typeof(SpanProbeCommand));
        var behavior = CreateBehavior<SpanProbeCommand>();
        var request = new SpanProbeCommand().WithContext(new TestRateLimitedContext("span-throttled"));

        for (var i = 0; i < MaxTokens; i++) await behavior.Handle(request, _ => Task.FromResult<object>("ok"), CancellationToken.None);
        Func<Task> act = () => behavior.Handle(request, _ => Task.FromResult<object>("ok"), CancellationToken.None);
        await act.Should().ThrowAsync<RateLimitExceededException>();

        var throttled = spans.Stopped.Where(a => a.OperationName == "RateLimiting.Check").Last();
        throttled.Status.Should().Be(ActivityStatusCode.Error);
        throttled.GetTagItem(CqrsTelemetry.Tags.RateLimitRetryAfterMilliseconds).Should().Be(1000d);
    }

    private sealed class SpanProbeCommand : RequestBase<IRateLimitedContext>;

    private sealed class PlainSpanProbeCommand : IRequest
    {
        public IRequestContext? Context { get; set; }
    }

    // Records the CQRSharp.Pipelines spans of one request type: the listener is process-wide, and other tests run in
    // parallel.
    private sealed class PipelineSpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _stopped = [];

        public PipelineSpanRecorder(Type requestType)
        {
            var requestTypeName = requestType.FullName;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == CqrsTelemetry.PipelinesActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (Equals(activity.GetTagItem(CqrsTelemetry.Tags.RequestType), requestTypeName))
                        lock (_stopped) _stopped.Add(activity);
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Stopped
        {
            get
            {
                lock (_stopped) return _stopped.ToArray();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
