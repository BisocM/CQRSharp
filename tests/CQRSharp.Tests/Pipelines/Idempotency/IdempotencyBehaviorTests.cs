using System.Security.Cryptography;
using System.Text;
using CQRSharp.Core.Pipelines;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     The idempotency behavior over the shipped in-memory store: requests that are not idempotent pass through, a
///     duplicate is replayed or rejected, a request that did not complete releases its claim, a key reused for another
///     payload is a mismatch, and a store or serializer that fails never replaces the request's own outcome.
/// </summary>
public sealed class IdempotencyBehaviorTests
{
    private static readonly IOptions<UnitOfWorkOptions> RollingBack = Options.Create(new UnitOfWorkOptions());
    private static readonly IOptions<UnitOfWorkOptions> CommittingFailures = Options.Create(new UnitOfWorkOptions { RollbackOnFailedResult = false });

    private static IdempotencyBehavior<TRequest, TResult> Create<TRequest, TResult>(
        IIdempotencyStore store,
        IOptions<UnitOfWorkOptions>? unitOfWork = null,
        IIdempotencyResultSerializer? serializer = null)
        where TRequest : IRequest
        => new(NullLogger<IdempotencyBehavior<TRequest, TResult>>.Instance, store, unitOfWork ?? RollingBack, serializer);

    [Fact(DisplayName = "Non-idempotent request passes through untouched")]
    public async Task NonIdempotent_PassesThrough()
    {
        var store = new FaultyIdempotencyStore();
        var behavior = Create<PlainRequest, object>(store);
        var calls = 0;

        var result = await behavior.Handle(new PlainRequest(),
            _ =>
            {
                calls++;
                return Task.FromResult<object>("ok");
            }, CancellationToken.None);

        result.Should().Be("ok");
        calls.Should().Be(1);
        store.Fingerprints.Should().BeEmpty("nothing is claimed for a request that is not idempotent");
    }

    [Fact(DisplayName = "A duplicate of a completed request whose result cannot be replayed is rejected as completed, not as in progress")]
    public async Task Duplicate_IsRejected()
    {
        var behavior = Create<IdempotentRequest, object>(new FaultyIdempotencyStore());
        var calls = 0;
        RequestHandlerDelegate<object> next = _ =>
        {
            calls++;
            return Task.FromResult<object>("ok");
        };

        (await behavior.Handle(new IdempotentRequest("k1"), next, CancellationToken.None)).Should().Be("ok");

        Func<Task> second = () => behavior.Handle(new IdempotentRequest("k1"), next, CancellationToken.None);
        (await second.Should().ThrowAsync<DuplicateRequestException>()).Which.IsInProgress.Should().BeFalse("the first request completed");
        calls.Should().Be(1, "the duplicate must not reach the handler");
    }

    [Fact(DisplayName = "A failed request releases its claim so it can be retried")]
    public async Task Failure_ReleasesClaim()
    {
        var behavior = Create<IdempotentRequest, object>(new FaultyIdempotencyStore());

        Func<Task> firstFails = () => behavior.Handle(new IdempotentRequest("k2"),
            _ => throw new InvalidOperationException("boom"), CancellationToken.None);
        await firstFails.Should().ThrowAsync<InvalidOperationException>();

        // The claim was released on failure, so a fresh attempt with the same key is allowed to proceed.
        var retry = await behavior.Handle(new IdempotentRequest("k2"),
            _ => Task.FromResult<object>("ok"), CancellationToken.None);
        retry.Should().Be("ok");
    }

    [Fact(DisplayName = "A request that returns a failed CommandResult releases its claim so the caller can retry")]
    public async Task FailedResult_ReleasesClaim()
    {
        var behavior = Create<IdempotentRequest, object>(new FaultyIdempotencyStore());

        var first = await behavior.Handle(new IdempotentRequest("k3"),
            _ => Task.FromResult<object>(CommandResult.FromError("declined")), CancellationToken.None);
        first.Should().BeOfType<CommandResult>().Which.IsSuccess.Should().BeFalse();

        var retry = await behavior.Handle(new IdempotentRequest("k3"),
            _ => Task.FromResult<object>("ok"), CancellationToken.None);
        retry.Should().Be("ok", "a failed result is not a completed request, so it must not be remembered as one");
    }

    [Fact(DisplayName = "A duplicate of a completed command is answered with the original success, and the handler does not run again")]
    public async Task CompletedCommand_IsReplayed()
    {
        var behavior = Create<IdempotentRequest, CommandResult>(new FaultyIdempotencyStore());
        var runs = 0;
        Task<CommandResult> Handler(CancellationToken _)
        {
            runs++;
            return Task.FromResult(CommandResult.FromSuccess());
        }

        (await behavior.Handle(new IdempotentRequest("pay-1"), Handler, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var replayed = await behavior.Handle(new IdempotentRequest("pay-1"), Handler, CancellationToken.None);

        replayed.IsSuccess.Should().BeTrue();
        runs.Should().Be(1, "the duplicate must be answered from the stored outcome, not by running the work again");
    }

    [Fact(DisplayName = "A value-carrying result is replayed through the configured serializer")]
    public async Task ValueResult_IsReplayedThroughTheSerializer()
    {
        var behavior = Create<IdempotentRequest, string>(new FaultyIdempotencyStore(), serializer: new Utf8StringSerializer());
        var runs = 0;

        var first = await behavior.Handle(new IdempotentRequest("q-1"), _ => Task.FromResult($"receipt-{++runs}"), CancellationToken.None);
        var second = await behavior.Handle(new IdempotentRequest("q-1"), _ => Task.FromResult($"receipt-{++runs}"), CancellationToken.None);

        first.Should().Be("receipt-1");
        second.Should().Be("receipt-1", "the duplicate gets the ORIGINAL result");
        runs.Should().Be(1);
    }

    [Fact(DisplayName = "Without a serializer a value-carrying duplicate is rejected")]
    public async Task ValueResult_WithoutSerializer_IsRejected()
    {
        var behavior = Create<IdempotentRequest, string>(new FaultyIdempotencyStore());

        await behavior.Handle(new IdempotentRequest("q-2"), _ => Task.FromResult("receipt"), CancellationToken.None);
        var duplicate = () => behavior.Handle(new IdempotentRequest("q-2"), _ => Task.FromResult("again"), CancellationToken.None);

        (await duplicate.Should().ThrowAsync<DuplicateRequestException>()).Which.IsInProgress.Should().BeFalse();
    }

    [Fact(DisplayName = "A duplicate of a request that is still running is rejected as in-progress")]
    public async Task InFlightDuplicate_IsRejectedAsInProgress()
    {
        var behavior = Create<IdempotentRequest, CommandResult>(new FaultyIdempotencyStore());
        var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = behavior.Handle(new IdempotentRequest("slow-1"), _ => release.Task, CancellationToken.None);
        var duplicate = () => behavior.Handle(new IdempotentRequest("slow-1"), _ => Task.FromResult(CommandResult.FromSuccess()), CancellationToken.None);

        (await duplicate.Should().ThrowAsync<DuplicateRequestException>()).Which.IsInProgress.Should().BeTrue();
        release.SetResult(CommandResult.FromSuccess());
        await first;
    }

    [Fact(DisplayName = "A failed result its unit of work commits keeps the claim: a duplicate gets the same failure back and the work is not repeated")]
    public async Task CommittedFailure_KeepsTheClaim_AndIsReplayed()
    {
        var behavior = Create<TransactionalIdempotentRequest, CommandResult>(new FaultyIdempotencyStore(), CommittingFailures);
        var runs = 0;
        Task<CommandResult> Handler(CancellationToken _)
        {
            runs++;
            return Task.FromResult(CommandResult.FromError(CommandErrorKind.Conflict, "card declined", 402));
        }

        var first = await behavior.Handle(new TransactionalIdempotentRequest("charge-1"), Handler, CancellationToken.None);
        var duplicate = await behavior.Handle(new TransactionalIdempotentRequest("charge-1"), Handler, CancellationToken.None);

        runs.Should().Be(1, "the committed failure stands; running it again is what the key prevents");
        duplicate.Should().Be(first);
        duplicate.ErrorKind.Should().Be(CommandErrorKind.Conflict);
        duplicate.ErrorCode.Should().Be(402);
    }

    [Fact(DisplayName = "A committed validation failure is replayed with its validation failures")]
    public async Task CommittedValidationFailure_IsReplayedWholly()
    {
        var behavior = Create<TransactionalIdempotentRequest, CommandResult>(new FaultyIdempotencyStore(), CommittingFailures);
        var failure = CommandResult.Invalid([new ValidationFailure("Amount.Range", "Too large", "Amount"), new ValidationFailure("Card.Expired", "Expired")], "Rejected", 7);

        await behavior.Handle(new TransactionalIdempotentRequest("charge-2"), _ => Task.FromResult(failure), CancellationToken.None);
        var duplicate = await behavior.Handle(new TransactionalIdempotentRequest("charge-2"), _ => Task.FromResult(CommandResult.FromSuccess()), CancellationToken.None);

        duplicate.Should().Be(failure);
    }

    [Fact(DisplayName = "A failed result of a transactional command is released when its unit of work rolls failures back")]
    public async Task RolledBackFailure_ReleasesTheClaim()
    {
        var behavior = Create<TransactionalIdempotentRequest, CommandResult>(new FaultyIdempotencyStore());

        await behavior.Handle(new TransactionalIdempotentRequest("charge-3"), _ => Task.FromResult(CommandResult.FromError("declined")), CancellationToken.None);
        var retry = await behavior.Handle(new TransactionalIdempotentRequest("charge-3"), _ => Task.FromResult(CommandResult.FromSuccess()), CancellationToken.None);

        retry.IsSuccess.Should().BeTrue("the rolled-back failure did no work, so the caller's retry runs");
    }

    [Fact(DisplayName = "A committed failure of a value-carrying command without a serializer rejects the duplicate rather than running it")]
    public async Task CommittedValueFailure_WithoutSerializer_IsRejected()
    {
        var behavior = Create<TransactionalIdempotentRequest, CommandResult<int>>(new FaultyIdempotencyStore(), CommittingFailures);
        var runs = 0;

        await behavior.Handle(new TransactionalIdempotentRequest("mint-1"), _ => Task.FromResult(CommandResult<int>.FromError($"quota {++runs}")), CancellationToken.None);
        var duplicate = () => behavior.Handle(new TransactionalIdempotentRequest("mint-1"), _ => Task.FromResult(CommandResult<int>.FromError($"quota {++runs}")), CancellationToken.None);

        (await duplicate.Should().ThrowAsync<DuplicateRequestException>()).Which.IsInProgress.Should().BeFalse();
        runs.Should().Be(1);
    }

    [Fact(DisplayName = "A stored result a plain command cannot read back rejects the duplicate instead of replaying a success")]
    public async Task UnreadableStoredResult_IsRejected()
    {
        var store = new FaultyIdempotencyStore();
        var claim = await store.TryClaimAsync("odd-1", null, CancellationToken.None);
        await store.CompleteAsync("odd-1", claim.Token!, [0xFF, 0x01], CancellationToken.None);
        var behavior = Create<IdempotentRequest, CommandResult>(store);

        var duplicate = () => behavior.Handle(new IdempotentRequest("odd-1"), _ => Task.FromResult(CommandResult.FromSuccess()), CancellationToken.None);

        (await duplicate.Should().ThrowAsync<DuplicateRequestException>()).Which.IsInProgress.Should().BeFalse();
    }

    [Fact(DisplayName = "An empty idempotency key fails the request before anything is claimed")]
    public async Task An_empty_key_fails_before_the_claim()
    {
        var store = new FaultyIdempotencyStore();
        var behavior = Create<IdempotentRequest, object>(store);
        var calls = 0;

        var act = () => behavior.Handle(new IdempotentRequest(""), _ =>
        {
            calls++;
            return Task.FromResult<object>("ok");
        }, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{nameof(IdempotentRequest)}*{nameof(IIdempotentRequest.IdempotencyKey)}*");
        store.Fingerprints.Should().BeEmpty("nothing may be claimed under an empty key");
        calls.Should().Be(0);
    }

    [Fact(DisplayName = "A handler failure stays the caller's failure when releasing the claim fails too")]
    public async Task A_failing_release_never_replaces_the_handlers_exception()
    {
        var store = new FaultyIdempotencyStore { ReleaseFailure = new TimeoutException("store timed out") };
        var logger = new CapturingLogger<IdempotencyBehavior<IdempotentRequest, object>>();
        var behavior = new IdempotencyBehavior<IdempotentRequest, object>(logger, store, RollingBack);

        var act = () => behavior.Handle(new IdempotentRequest("release-fails"), _ => throw new InvalidOperationException("the handler's own failure"), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("the handler's own failure");
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4104).Which.Exception.Should().BeOfType<TimeoutException>();
    }

    [Fact(DisplayName = "A successful result is returned when recording its completion fails")]
    public async Task A_failing_completion_never_replaces_the_result()
    {
        var store = new FaultyIdempotencyStore { CompleteFailure = new TimeoutException("store timed out") };
        var logger = new CapturingLogger<IdempotencyBehavior<IdempotentRequest, CommandResult>>();
        var behavior = new IdempotencyBehavior<IdempotentRequest, CommandResult>(logger, store, RollingBack);

        var result = await behavior.Handle(new IdempotentRequest("complete-fails"), _ => Task.FromResult(CommandResult.FromSuccess()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the work is done; only its bookkeeping failed");
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 4103).Which.Exception.Should().BeOfType<TimeoutException>();
    }

    [Fact(DisplayName = "A result the serializer throws on is still returned; the key completes without a replay, so a duplicate is rejected")]
    public async Task A_throwing_serializer_never_replaces_the_result()
    {
        var logger = new CapturingLogger<IdempotencyBehavior<IdempotentRequest, string>>();
        var behavior = new IdempotencyBehavior<IdempotentRequest, string>(logger, new FaultyIdempotencyStore(), RollingBack, new ThrowingSerializer());
        var runs = 0;

        var result = await behavior.Handle(new IdempotentRequest("serializer-throws"), _ => Task.FromResult($"receipt-{++runs}"), CancellationToken.None);
        var duplicate = () => behavior.Handle(new IdempotentRequest("serializer-throws"), _ => Task.FromResult($"receipt-{++runs}"), CancellationToken.None);

        result.Should().Be("receipt-1");
        (await duplicate.Should().ThrowAsync<DuplicateRequestException>()).Which.IsInProgress.Should().BeFalse("the work completed; only its replay is missing");
        runs.Should().Be(1);
        logger.Entries.Should().Contain(e => e.EventId.Id == 4103 && e.Exception is NotSupportedException);
    }

    [Fact(DisplayName = "A store that answers a claim with nothing fails the request before the handler runs, naming the store")]
    public async Task A_store_without_an_answer_fails_before_the_handler_runs()
    {
        var behavior = Create<IdempotentRequest, object>(new FaultyIdempotencyStore { AnswersNoClaim = true });
        var calls = 0;

        var act = () => behavior.Handle(new IdempotentRequest("no-answer"), _ =>
        {
            calls++;
            return Task.FromResult<object>("ok");
        }, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{nameof(FaultyIdempotencyStore)}*");
        calls.Should().Be(0, "a claim the behavior cannot complete or release must never let the handler's side effects happen");
    }

    [Fact(DisplayName = "A request's own fingerprint reaches the store as a SHA-256 digest of its type and its text, whatever its length; an empty or null one disables the check")]
    public async Task A_custom_fingerprint_reaches_the_store_as_its_digest()
    {
        var store = new FaultyIdempotencyStore();
        var behavior = Create<FingerprintedRequest, object>(store);
        var longFingerprint = new string('x', 1000);

        await behavior.Handle(new FingerprintedRequest("fp-1", longFingerprint), _ => Task.FromResult<object>("ok"), CancellationToken.None);
        await behavior.Handle(new FingerprintedRequest("fp-2", ""), _ => Task.FromResult<object>("ok"), CancellationToken.None);
        await behavior.Handle(new FingerprintedRequest("fp-3", null), _ => Task.FromResult<object>("ok"), CancellationToken.None);

        store.Fingerprints.Should().Equal(Digest($"{typeof(FingerprintedRequest)}\0{longFingerprint}"), null, null);
        store.Fingerprints[0].Should().HaveLength(64, "the digest fits the column a store sizes for the generated fingerprints");
    }

    [Fact(DisplayName = "Two request types whose own fingerprints read the same are a mismatch under one key, not a replay of each other; the rejection is logged at Information")]
    public async Task Own_fingerprints_are_scoped_by_the_request_type()
    {
        var store = new FaultyIdempotencyStore();
        var logger = new CapturingLogger<IdempotencyBehavior<RefundFunds, CommandResult>>();
        var transfer = Create<TransferFunds, CommandResult>(store);
        var refund = new IdempotencyBehavior<RefundFunds, CommandResult>(logger, store, RollingBack);
        var refunds = 0;

        (await transfer.Handle(new TransferFunds("pay-7", 100.5m), _ => Task.FromResult(CommandResult.FromSuccess()), CancellationToken.None)).IsSuccess.Should().BeTrue();
        var reused = () => refund.Handle(new RefundFunds("pay-7", 100.5m), _ =>
        {
            refunds++;
            return Task.FromResult(CommandResult.FromSuccess());
        }, CancellationToken.None);

        (await reused.Should().ThrowAsync<IdempotencyKeyMismatchException>()).Which.IdempotencyKey.Should().Be("pay-7");
        refunds.Should().Be(0, "a refund must never be answered with the transfer's success");
        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4102 && e.Level == LogLevel.Information && e.Exception == null);
    }

    [Fact(DisplayName = "A result type another serializer could not store is still reported for this one: the report is per serializer, not per process")]
    public async Task A_result_the_serializer_cannot_store_is_reported_per_serializer()
    {
        var first = new CapturingLogger<IdempotencyBehavior<UnreplayableRequest, object>>();
        var second = new CapturingLogger<IdempotencyBehavior<UnreplayableRequest, object>>();
        var behaviorOfOneHost = new IdempotencyBehavior<UnreplayableRequest, object>(first, new FaultyIdempotencyStore(), RollingBack, new Utf8StringSerializer());
        var behaviorOfAnother = new IdempotencyBehavior<UnreplayableRequest, object>(second, new FaultyIdempotencyStore(), RollingBack, new Utf8StringSerializer());

        await behaviorOfOneHost.Handle(new UnreplayableRequest("per-serializer-1"), _ => Task.FromResult<object>(42), CancellationToken.None);
        await behaviorOfAnother.Handle(new UnreplayableRequest("per-serializer-1"), _ => Task.FromResult<object>(42), CancellationToken.None);

        first.Entries.Should().ContainSingle(e => e.EventId.Id == 4109);
        second.Entries.Should().ContainSingle(e => e.EventId.Id == 4109);
    }

    [Theory(DisplayName = "A stored failure record that cannot be read rejects the duplicate instead of throwing what the reader hit")]
    [InlineData(new byte[] { 0x01, 0, 0, 0, 0, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x7F })]
    [InlineData(new byte[] { 0x01, 1, 0, 0, 0, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })]
    public async Task Unreadable_failure_record_rejects_the_duplicate(byte[] record)
    {
        var behavior = Create<IdempotentCommand, CommandResult>(new CompletedWith(record));
        var calls = 0;

        var duplicate = () => behavior.Handle(new IdempotentCommand("corrupt"), _ =>
        {
            calls++;
            return Task.FromResult(CommandResult.FromSuccess());
        }, CancellationToken.None);

        (await duplicate.Should().ThrowAsync<DuplicateRequestException>()).Which.IsInProgress.Should().BeFalse();
        calls.Should().Be(0);
    }

    [Fact(DisplayName = "A result the configured serializer cannot store is reported once, and its duplicate is rejected")]
    public async Task A_result_the_serializer_cannot_store_is_reported_once()
    {
        var logger = new CapturingLogger<IdempotencyBehavior<UnreplayableRequest, object>>();
        var behavior = new IdempotencyBehavior<UnreplayableRequest, object>(logger, new FaultyIdempotencyStore(), RollingBack, new Utf8StringSerializer());

        // The serializer stores strings only; this handler returns a number.
        await behavior.Handle(new UnreplayableRequest("unreplayable-1"), _ => Task.FromResult<object>(42), CancellationToken.None);
        await behavior.Handle(new UnreplayableRequest("unreplayable-2"), _ => Task.FromResult<object>(42), CancellationToken.None);
        var duplicate = () => behavior.Handle(new UnreplayableRequest("unreplayable-1"), _ => Task.FromResult<object>(42), CancellationToken.None);

        await duplicate.Should().ThrowAsync<DuplicateRequestException>();
        logger.Entries.Where(e => e.EventId.Id == 4109).Should().ContainSingle()
            .Which.Message.Should().Contain(nameof(UnreplayableRequest)).And.Contain(nameof(Utf8StringSerializer));
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class Utf8StringSerializer : IIdempotencyResultSerializer
    {
        public bool TrySerialize<TResult>(TResult result, out byte[] payload)
        {
            payload = result is string s ? Encoding.UTF8.GetBytes(s) : [];
            return result is string;
        }

        public bool TryDeserialize<TResult>(byte[] payload, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TResult result)
        {
            if (typeof(TResult) != typeof(string))
            {
                result = default;
                return false;
            }

            result = (TResult)(object)Encoding.UTF8.GetString(payload);
            return true;
        }
    }

    private sealed class ThrowingSerializer : IIdempotencyResultSerializer
    {
        public bool TrySerialize<TResult>(TResult result, out byte[] payload) => throw new NotSupportedException("the serializer failed");

        public bool TryDeserialize<TResult>(byte[] payload, out TResult result) => throw new NotSupportedException("the serializer failed");
    }

    private sealed class FingerprintedRequest(string key, string? fingerprint) : IFingerprintedRequest
    {
        public string IdempotencyKey { get; } = key;
        public string? Fingerprint { get; } = fingerprint;
        public IRequestContext? Context { get; set; }
    }

    private sealed class TransferFunds(string key, decimal amount) : IFingerprintedRequest
    {
        public string IdempotencyKey { get; } = key;
        public string Fingerprint => FormattableString.Invariant($"amount:{amount}");
        public IRequestContext? Context { get; set; }
    }

    private sealed class RefundFunds(string key, decimal amount) : IFingerprintedRequest
    {
        public string IdempotencyKey { get; } = key;
        public string Fingerprint => FormattableString.Invariant($"amount:{amount}");
        public IRequestContext? Context { get; set; }
    }

    // Its own type, so the once-per-request-type report is not spent by another test.
    private sealed class UnreplayableRequest(string key) : IIdempotentRequest
    {
        public string IdempotencyKey { get; } = key;
        public IRequestContext? Context { get; set; }
    }

    private sealed class IdempotentRequest(string key) : IIdempotentRequest
    {
        public string IdempotencyKey { get; } = key;
        public IRequestContext? Context { get; set; }
    }

    private sealed class TransactionalIdempotentRequest(string key) : IIdempotentRequest, ITransactionalCommand
    {
        public string IdempotencyKey { get; } = key;
        public System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.Unspecified;
        public IRequestContext? Context { get; set; }
    }

    private sealed class PlainRequest : IRequest
    {
        public IRequestContext? Context { get; set; }
    }

    private sealed class IdempotentCommand(string key) : IIdempotentRequest, ICommand
    {
        public string IdempotencyKey { get; } = key;
        public IRequestContext? Context { get; set; }
    }

    // Answers every claim as already completed with the given stored record.
    private sealed class CompletedWith(byte[] record) : IIdempotencyStore
    {
        public Task<IdempotencyClaim> TryClaimAsync(string key, string? fingerprint, CancellationToken cancellationToken)
            => Task.FromResult(IdempotencyClaim.Completed(record));

        public Task CompleteAsync(string key, string claimToken, byte[]? result, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReleaseAsync(string key, string claimToken, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
