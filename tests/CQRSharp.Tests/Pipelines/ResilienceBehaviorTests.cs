using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Models.Requests;
using CQRSharp.Pipelines.Behaviors.Resilience;
using CQRSharp.Pipelines.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Regression tests for the resilience behavior's retry policy: retries are opt-in via
///     <see cref="IRetryableRequest" />, and caller cancellation and timeouts are never retried.
/// </summary>
public class ResilienceBehaviorTests
{
    private static ResilienceBehavior<TRequest, object> CreateBehavior<TRequest>(int maxRetries = 3)
        where TRequest : IRequest
        => new(
            NullLogger<ResilienceBehavior<TRequest, object>>.Instance,
            Options.Create(new ResilienceOptions { MaxRetries = maxRetries, BaseDelay = TimeSpan.Zero }));

    [Fact(DisplayName = "Non-retryable request: handler failure is propagated without retrying")]
    public async Task NonRetryable_DoesNotRetry()
    {
        var behavior = CreateBehavior<PlainRequest>();
        var calls = 0;

        Func<Task> act = () => behavior.Handle(
            new PlainRequest(),
            _ =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        calls.Should().Be(1, "a non-IRetryableRequest must not be retried");
    }

    [Fact(DisplayName = "Retryable request: transient failures are retried until success")]
    public async Task Retryable_RetriesUntilSuccess()
    {
        var behavior = CreateBehavior<RetryableRequest>(3);
        var calls = 0;

        var result = await behavior.Handle(
            new RetryableRequest(),
            _ =>
            {
                calls++;
                if (calls < 3) throw new InvalidOperationException("transient");
                return Task.FromResult<object>("ok");
            },
            CancellationToken.None);

        result.Should().Be("ok");
        calls.Should().Be(3);
    }

    [Fact(DisplayName = "Caller cancellation is never retried, even for a retryable request")]
    public async Task Cancellation_IsNotRetried()
    {
        var behavior = CreateBehavior<RetryableRequest>();
        var calls = 0;

        Func<Task> act = () => behavior.Handle(
            new RetryableRequest(),
            _ =>
            {
                calls++;
                throw new OperationCanceledException();
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1, "cancellation is terminal and must not be retried");
    }

    [Fact(DisplayName = "Timeouts are never retried, even for a retryable request")]
    public async Task Timeout_IsNotRetried()
    {
        var behavior = CreateBehavior<RetryableRequest>();
        var calls = 0;

        Func<Task> act = () => behavior.Handle(
            new RetryableRequest(),
            _ =>
            {
                calls++;
                throw new TimeoutException();
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        calls.Should().Be(1, "retrying a timeout would multiply the configured time budget");
    }

    private sealed class PlainRequest : IRequest
    {
        public IRequestContext? Context { get; set; }
        public RequestMetadata? Metadata { get; set; }
    }

    private sealed class RetryableRequest : IRetryableRequest
    {
        public IRequestContext? Context { get; set; }
        public RequestMetadata? Metadata { get; set; }
    }
}