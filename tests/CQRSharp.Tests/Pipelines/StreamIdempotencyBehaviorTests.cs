using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CQRSharp.Pipelines;
using CQRSharp.Pipelines.Behaviors.Idempotency;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Streaming requests used to get no idempotency enforcement at all: only the command/query behavior was registered,
///     so an <see cref="IIdempotentRequest" /> stream was silently unprotected.
/// </summary>
public sealed class StreamIdempotencyBehaviorTests
{
    [Fact(DisplayName = "A completed idempotent stream keeps its claim: the same key is rejected as a duplicate")]
    public async Task Completed_stream_rejects_a_duplicate()
    {
        var behavior = Create(new Store());

        (await Drain(behavior.Handle(new Request("s1"), Items, CancellationToken.None))).Should().Equal(1, 2, 3);

        var duplicate = () => Drain(behavior.Handle(new Request("s1"), Items, CancellationToken.None));
        await duplicate.Should().ThrowAsync<DuplicateRequestException>();
    }

    [Fact(DisplayName = "A faulted idempotent stream releases its claim so it can be retried")]
    public async Task Faulted_stream_releases_its_claim()
    {
        var behavior = Create(new Store());

        var faulted = () => Drain(behavior.Handle(new Request("s2"), Faulting, CancellationToken.None));
        await faulted.Should().ThrowAsync<InvalidOperationException>();

        (await Drain(behavior.Handle(new Request("s2"), Items, CancellationToken.None))).Should().Equal(1, 2, 3);
    }

    [Fact(DisplayName = "A stream its consumer abandons early releases its claim")]
    public async Task Abandoned_stream_releases_its_claim()
    {
        var behavior = Create(new Store());

        await foreach (var _ in behavior.Handle(new Request("s3"), Items, CancellationToken.None))
            break;

        (await Drain(behavior.Handle(new Request("s3"), Items, CancellationToken.None))).Should().Equal(1, 2, 3);
    }

    private static StreamIdempotencyBehavior<Request, int> Create(IIdempotencyStore store)
        => new(NullLogger<StreamIdempotencyBehavior<Request, int>>.Instance, store);

    private static async IAsyncEnumerable<int> Items([EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 1; i <= 3; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }

    private static async IAsyncEnumerable<int> Faulting([EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return 1;
        await Task.Yield();
        throw new InvalidOperationException("stream fault");
    }

    private static async Task<List<int>> Drain(IAsyncEnumerable<int> source)
    {
        var items = new List<int>();
        await foreach (var item in source) items.Add(item);
        return items;
    }

    private sealed class Store : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, byte> _claimed = new();

        public Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_claimed.TryAdd(key, 0));

        public Task ReleaseAsync(string key, CancellationToken cancellationToken)
        {
            _claimed.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class Request(string key) : IIdempotentRequest
    {
        public string IdempotencyKey { get; } = key;
        public IRequestContext? Context { get; set; }
        public RequestMetadata? Metadata { get; set; }
    }
}
