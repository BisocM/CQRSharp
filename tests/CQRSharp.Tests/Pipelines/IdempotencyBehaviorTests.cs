using System.Collections.Concurrent;
using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Idempotency;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Models.Idempotency;
using CQRSharp.Abstractions.Models.Requests;
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
    private sealed class InMemoryStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, byte> _claimed = new();

        public Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(_claimed.TryAdd(key, 0));

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

    private static IdempotencyBehavior<TRequest, object> Create<TRequest>(IIdempotencyStore store)
        where TRequest : IRequest
        => new(NullLogger<IdempotencyBehavior<TRequest, object>>.Instance, store);

    [Fact(DisplayName = "Non-idempotent request passes through untouched")]
    public async Task NonIdempotent_PassesThrough()
    {
        var behavior = Create<PlainRequest>(new InMemoryStore());
        var calls = 0;

        var result = await behavior.Handle(new PlainRequest(),
            _ => { calls++; return Task.FromResult<object>("ok"); }, CancellationToken.None);

        result.Should().Be("ok");
        calls.Should().Be(1);
    }

    [Fact(DisplayName = "Duplicate request is rejected with DuplicateRequestException")]
    public async Task Duplicate_IsRejected()
    {
        var behavior = Create<IdempotentRequest>(new InMemoryStore());
        var calls = 0;
        Func<CancellationToken, Task<object>> next = _ => { calls++; return Task.FromResult<object>("ok"); };

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
}
