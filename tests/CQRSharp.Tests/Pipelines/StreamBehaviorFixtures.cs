using System.Runtime.CompilerServices;

namespace CQRSharp.Tests.Pipelines;

/// <summary>Stream sources, a drain helper and a plain request shared by the streaming behavior tests.</summary>
internal static class StreamBehaviorFixtures
{
    /// <summary>Yields the given items, honoring cancellation between each, with a tiny async hop.</summary>
    public static async IAsyncEnumerable<int> Produce(
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

    /// <summary>
    ///     Yields <paramref name="before" /> as <see cref="Produce" /> does, then throws <paramref name="failure" />. With no
    ///     items the stream fails on its first <c>MoveNextAsync</c>, before any asynchronous hop, so everything a behavior
    ///     does in reaction to the failure has happened by the time the consumer's first await returns.
    /// </summary>
    public static async IAsyncEnumerable<int> ThrowsAt(
        IEnumerable<int> before,
        Exception failure,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in Produce(before, cancellationToken).ConfigureAwait(false))
            yield return item;

        throw failure;
    }

    /// <summary>
    ///     Yields <paramref name="items" />, then fails with <paramref name="failure" /> when one is given, and throws
    ///     <paramref name="disposalFailure" /> from its enumerator's disposal.
    /// </summary>
    public static IAsyncEnumerable<int> FailsOnDisposal(int[] items, Exception? failure, Exception disposalFailure)
        => new DisposalFailingStream(items, failure, disposalFailure);

    /// <summary>Drains an async stream into a list.</summary>
    public static async Task<List<int>> Drain(IAsyncEnumerable<int> stream)
    {
        var results = new List<int>();
        await foreach (var item in stream.ConfigureAwait(false))
            results.Add(item);
        return results;
    }

    /// <summary>A plain (non-retryable) streaming request.</summary>
    internal sealed class StreamPlainRequest : IRequest
    {
        public IRequestContext? Context { get; set; }
    }

    private sealed class DisposalFailingStream(int[] items, Exception? failure, Exception disposalFailure) : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(items, failure, disposalFailure);

        private sealed class Enumerator(int[] items, Exception? failure, Exception disposalFailure) : IAsyncEnumerator<int>
        {
            private int _next;

            public int Current { get; private set; }

            public ValueTask<bool> MoveNextAsync()
            {
                if (_next < items.Length)
                {
                    Current = items[_next++];
                    return ValueTask.FromResult(true);
                }

                return failure is null ? ValueTask.FromResult(false) : ValueTask.FromException<bool>(failure);
            }

            public ValueTask DisposeAsync() => ValueTask.FromException(disposalFailure);
        }
    }
}
