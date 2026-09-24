using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static CQRSharp.Tests.Pipelines.StreamBehaviorFixtures;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     The streaming idempotency behavior over the shipped in-memory store: a stream that runs to completion keeps its
///     claim, one that faults or is abandoned releases it, a key reused for another payload is a mismatch, and a store
///     that fails never replaces the stream's own outcome.
/// </summary>
public sealed class StreamIdempotencyBehaviorTests
{
    [Fact(DisplayName = "A completed idempotent stream completes its claim: the same key is rejected as completed, not as in progress")]
    public async Task Completed_stream_rejects_a_duplicate()
    {
        var behavior = Create(new FaultyIdempotencyStore());

        (await Drain(behavior.Handle(new Request("s1"), Items, CancellationToken.None))).Should().Equal(1, 2, 3);

        var duplicate = () => Drain(behavior.Handle(new Request("s1"), Items, CancellationToken.None));
        (await duplicate.Should().ThrowAsync<DuplicateRequestException>()).Which.IsInProgress.Should().BeFalse("the first stream ran to completion");
    }

    [Fact(DisplayName = "A faulted idempotent stream releases its claim so it can be retried")]
    public async Task Faulted_stream_releases_its_claim()
    {
        var behavior = Create(new FaultyIdempotencyStore());

        var faulted = () => Drain(behavior.Handle(new Request("s2"), Faulting, CancellationToken.None));
        await faulted.Should().ThrowAsync<InvalidOperationException>();

        (await Drain(behavior.Handle(new Request("s2"), Items, CancellationToken.None))).Should().Equal(1, 2, 3);
    }

    [Fact(DisplayName = "A stream that faults and then fails to dispose surfaces its own failure and releases its claim; the disposal failure is logged")]
    public async Task Disposal_failure_after_a_fault_keeps_the_fault()
    {
        var logger = new CapturingLogger<StreamIdempotencyBehavior<Request, int>>();
        var behavior = new StreamIdempotencyBehavior<Request, int>(logger, new FaultyIdempotencyStore());
        var failure = new InvalidOperationException("stream failed");
        var disposalFailure = new IOException("disposal failed");

        var faulted = () => Drain(behavior.Handle(new Request("s-dispose-1"), _ => FailsOnDisposal([1], failure, disposalFailure), CancellationToken.None));

        (await faulted.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4110 && e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(disposalFailure);
        (await Drain(behavior.Handle(new Request("s-dispose-1"), Items, CancellationToken.None))).Should().Equal(1, 2, 3);
    }

    [Fact(DisplayName = "A stream that runs to its end and then fails to dispose surfaces the disposal failure and releases its claim, so a retry is not a duplicate")]
    public async Task Disposal_failure_after_the_end_releases_the_claim()
    {
        var behavior = Create(new FaultyIdempotencyStore());
        var disposalFailure = new IOException("disposal failed");

        var ended = () => Drain(behavior.Handle(new Request("s-dispose-2"), _ => FailsOnDisposal([1, 2], null, disposalFailure), CancellationToken.None));

        (await ended.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(disposalFailure);
        (await Drain(behavior.Handle(new Request("s-dispose-2"), Items, CancellationToken.None))).Should().Equal(1, 2, 3);
    }

    [Fact(DisplayName = "A stream its consumer abandons early releases its claim")]
    public async Task Abandoned_stream_releases_its_claim()
    {
        var behavior = Create(new FaultyIdempotencyStore());

        await foreach (var _ in behavior.Handle(new Request("s3"), Items, CancellationToken.None))
            break;

        (await Drain(behavior.Handle(new Request("s3"), Items, CancellationToken.None))).Should().Equal(1, 2, 3);
    }

    [Fact(DisplayName = "A streaming request reused under its key with a different payload is rejected with IdempotencyKeyMismatchException, logged at Information")]
    public async Task A_different_payload_under_the_same_key_is_a_mismatch()
    {
        var logger = new CapturingLogger<StreamIdempotencyBehavior<FingerprintedRequest, int>>();
        var behavior = new StreamIdempotencyBehavior<FingerprintedRequest, int>(logger, new FaultyIdempotencyStore());

        (await Drain(behavior.Handle(new FingerprintedRequest("s4", "export:orders"), Items, CancellationToken.None))).Should().Equal(1, 2, 3);
        var reused = () => Drain(behavior.Handle(new FingerprintedRequest("s4", "export:invoices"), Items, CancellationToken.None));

        (await reused.Should().ThrowAsync<IdempotencyKeyMismatchException>()).Which.IdempotencyKey.Should().Be("s4");
        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4106 && e.Level == LogLevel.Information && e.Exception == null);
    }

    [Fact(DisplayName = "An empty idempotency key fails the stream before anything is claimed or started")]
    public async Task An_empty_key_fails_before_the_claim()
    {
        var store = new FaultyIdempotencyStore();
        var behavior = Create(store);
        var started = false;

        var act = () => Drain(behavior.Handle(new Request(""), ct =>
        {
            started = true;
            return Items(ct);
        }, CancellationToken.None));

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{nameof(Request)}*{nameof(IIdempotentRequest.IdempotencyKey)}*");
        store.Fingerprints.Should().BeEmpty();
        started.Should().BeFalse();
    }

    [Fact(DisplayName = "A faulted stream surfaces its own exception when releasing the claim fails too")]
    public async Task A_failing_release_never_replaces_the_streams_exception()
    {
        var store = new FaultyIdempotencyStore { ReleaseFailure = new TimeoutException("store timed out") };
        var logger = new CapturingLogger<StreamIdempotencyBehavior<Request, int>>();
        var behavior = new StreamIdempotencyBehavior<Request, int>(logger, store);

        var faulted = () => Drain(behavior.Handle(new Request("s5"), Faulting, CancellationToken.None));

        (await faulted.Should().ThrowAsync<InvalidOperationException>()).WithMessage("stream fault");
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4108).Which.Exception.Should().BeOfType<TimeoutException>();
    }

    [Fact(DisplayName = "A completed stream is delivered whole when recording its completion fails")]
    public async Task A_failing_completion_never_replaces_the_streams_outcome()
    {
        var store = new FaultyIdempotencyStore { CompleteFailure = new TimeoutException("store timed out") };
        var logger = new CapturingLogger<StreamIdempotencyBehavior<Request, int>>();
        var behavior = new StreamIdempotencyBehavior<Request, int>(logger, store);

        (await Drain(behavior.Handle(new Request("s6"), Items, CancellationToken.None))).Should().Equal(1, 2, 3);
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4107).Which.Exception.Should().BeOfType<TimeoutException>();
    }

    [Fact(DisplayName = "A store that answers a stream's claim with nothing fails it before the stream starts, naming the store")]
    public async Task A_store_without_an_answer_fails_before_the_stream_starts()
    {
        var behavior = Create(new FaultyIdempotencyStore { AnswersNoClaim = true });
        var started = false;

        var act = () => Drain(behavior.Handle(new Request("no-answer"), ct =>
        {
            started = true;
            return Items(ct);
        }, CancellationToken.None));

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{nameof(FaultyIdempotencyStore)}*");
        started.Should().BeFalse();
    }

    private static StreamIdempotencyBehavior<Request, int> Create(IIdempotencyStore store)
        => new(NullLogger<StreamIdempotencyBehavior<Request, int>>.Instance, store);

    private static IAsyncEnumerable<int> Items(CancellationToken ct) => Produce([1, 2, 3], ct);

    private static IAsyncEnumerable<int> Faulting(CancellationToken ct) => ThrowsAt([1], new InvalidOperationException("stream fault"), ct);

    private sealed class Request(string key) : IIdempotentRequest
    {
        public string IdempotencyKey { get; } = key;
        public IRequestContext? Context { get; set; }
    }

    private sealed class FingerprintedRequest(string key, string fingerprint) : IFingerprintedRequest
    {
        public string IdempotencyKey { get; } = key;
        public string Fingerprint { get; } = fingerprint;
        public IRequestContext? Context { get; set; }
    }
}
