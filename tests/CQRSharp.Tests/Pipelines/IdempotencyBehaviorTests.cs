using System.Collections.Concurrent;
using CQRSharp.Pipelines;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Behaviors.Idempotency;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Tests for the idempotency behavior: non-idempotent requests pass through, duplicates are rejected, and a failed
///     request releases its claim so it can be retried.
/// </summary>
public class IdempotencyBehaviorTests
{
    private static IdempotencyBehavior<TRequest, object> Create<TRequest>(IIdempotencyStore store)
        where TRequest : IRequest
        => new(NullLogger<IdempotencyBehavior<TRequest, object>>.Instance, store);

    [Fact(DisplayName = "Non-idempotent request passes through untouched")]
    public async Task NonIdempotent_PassesThrough()
    {
        var behavior = Create<PlainRequest>(new InMemoryStore());
        var calls = 0;

        var result = await behavior.Handle(new PlainRequest(),
            _ =>
            {
                calls++;
                return Task.FromResult<object>("ok");
            }, CancellationToken.None);

        result.Should().Be("ok");
        calls.Should().Be(1);
    }

    [Fact(DisplayName = "Duplicate request is rejected with DuplicateRequestException")]
    public async Task Duplicate_IsRejected()
    {
        var behavior = Create<IdempotentRequest>(new InMemoryStore());
        var calls = 0;
        RequestHandlerDelegate<object> next = _ =>
        {
            calls++;
            return Task.FromResult<object>("ok");
        };

        (await behavior.Handle(new IdempotentRequest("k1"), next, CancellationToken.None)).Should().Be("ok");

        Func<Task> second = () => behavior.Handle(new IdempotentRequest("k1"), next, CancellationToken.None);
        await second.Should().ThrowAsync<DuplicateRequestException>();
        calls.Should().Be(1, "the duplicate must not reach the handler");
    }

    [Fact(DisplayName = "A failed request releases its claim so it can be retried")]
    public async Task Failure_ReleasesClaim()
    {
        var behavior = Create<IdempotentRequest>(new InMemoryStore());

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
        var behavior = Create<IdempotentRequest>(new InMemoryStore());

        var first = await behavior.Handle(new IdempotentRequest("k3"),
            _ => Task.FromResult<object>(CQRSharp.CommandResult.FromError("declined")), CancellationToken.None);
        first.Should().BeOfType<CQRSharp.CommandResult>().Which.IsSuccess.Should().BeFalse();

        var retry = await behavior.Handle(new IdempotentRequest("k3"),
            _ => Task.FromResult<object>("ok"), CancellationToken.None);
        retry.Should().Be("ok", "a failed result is not a completed request, so it must not be remembered as one");
    }

    [Fact(DisplayName = "A duplicate of a completed command is answered with the original success, and the handler does not run again")]
    public async Task CompletedCommand_IsReplayed()
    {
        var store = new ReplayStore();
        var behavior = new IdempotencyBehavior<IdempotentRequest, CommandResult>(
            NullLogger<IdempotencyBehavior<IdempotentRequest, CommandResult>>.Instance, store);
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
        var store = new ReplayStore();
        var behavior = new IdempotencyBehavior<IdempotentRequest, string>(
            NullLogger<IdempotencyBehavior<IdempotentRequest, string>>.Instance, store, new Utf8StringSerializer());
        var runs = 0;

        var first = await behavior.Handle(new IdempotentRequest("q-1"), _ => Task.FromResult($"receipt-{++runs}"), CancellationToken.None);
        var second = await behavior.Handle(new IdempotentRequest("q-1"), _ => Task.FromResult($"receipt-{++runs}"), CancellationToken.None);

        first.Should().Be("receipt-1");
        second.Should().Be("receipt-1", "the duplicate gets the ORIGINAL result");
        runs.Should().Be(1);
    }

    [Fact(DisplayName = "Without a serializer a value-carrying duplicate is rejected, as before 5.0")]
    public async Task ValueResult_WithoutSerializer_IsRejected()
    {
        var behavior = new IdempotencyBehavior<IdempotentRequest, string>(
            NullLogger<IdempotencyBehavior<IdempotentRequest, string>>.Instance, new ReplayStore());

        await behavior.Handle(new IdempotentRequest("q-2"), _ => Task.FromResult("receipt"), CancellationToken.None);
        var duplicate = () => behavior.Handle(new IdempotentRequest("q-2"), _ => Task.FromResult("again"), CancellationToken.None);

        (await duplicate.Should().ThrowAsync<DuplicateRequestException>()).Which.IsInProgress.Should().BeFalse();
    }

    [Fact(DisplayName = "A duplicate of a request that is still running is rejected as in-progress")]
    public async Task InFlightDuplicate_IsRejectedAsInProgress()
    {
        var behavior = new IdempotencyBehavior<IdempotentRequest, CommandResult>(
            NullLogger<IdempotencyBehavior<IdempotentRequest, CommandResult>>.Instance, new ReplayStore());
        var release = new TaskCompletionSource<CommandResult>();

        var first = behavior.Handle(new IdempotentRequest("slow-1"), _ => release.Task, CancellationToken.None);
        var duplicate = () => behavior.Handle(new IdempotentRequest("slow-1"), _ => Task.FromResult(CommandResult.FromSuccess()), CancellationToken.None);

        (await duplicate.Should().ThrowAsync<DuplicateRequestException>()).Which.IsInProgress.Should().BeTrue();
        release.SetResult(CommandResult.FromSuccess());
        await first;
    }

    // A store that honours the completed/in-progress distinction (the legacy InMemoryStore below only knows "claimed").
    private sealed class ReplayStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, (bool Completed, byte[]? Result)> _entries = new();

        public Task<IdempotencyClaim> TryClaimAsync(string key, CancellationToken cancellationToken)
        {
            if (_entries.TryAdd(key, (false, null))) return Task.FromResult(IdempotencyClaim.Claimed);
            var entry = _entries[key];
            return Task.FromResult(entry.Completed ? IdempotencyClaim.Completed(entry.Result) : IdempotencyClaim.InProgress);
        }

        public Task CompleteAsync(string key, byte[]? result, CancellationToken cancellationToken)
        {
            _entries[key] = (true, result);
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(string key, CancellationToken cancellationToken)
        {
            _entries.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class Utf8StringSerializer : IIdempotencyResultSerializer
    {
        public bool TrySerialize<TResult>(TResult result, out byte[] payload)
        {
            payload = result is string s ? System.Text.Encoding.UTF8.GetBytes(s) : [];
            return result is string;
        }

        public bool TryDeserialize<TResult>(byte[] payload, out TResult result)
        {
            if (typeof(TResult) != typeof(string))
            {
                result = default!;
                return false;
            }

            result = (TResult)(object)System.Text.Encoding.UTF8.GetString(payload);
            return true;
        }
    }

    private sealed class InMemoryStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, byte> _claimed = new();

        public Task<IdempotencyClaim> TryClaimAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(_claimed.TryAdd(key, 0) ? IdempotencyClaim.Claimed : IdempotencyClaim.InProgress);

        public Task CompleteAsync(string key, byte[]? result, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReleaseAsync(string key, CancellationToken cancellationToken)
        {
            _claimed.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class IdempotentRequest(string key) : IIdempotentRequest
    {
        public string IdempotencyKey { get; } = key;
        public IRequestContext? Context { get; set; }
        public RequestMetadata? Metadata { get; set; }
    }

    private sealed class PlainRequest : IRequest
    {
        public IRequestContext? Context { get; set; }
        public RequestMetadata? Metadata { get; set; }
    }
}