using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Stream;

namespace CQRSharp.Tests.Shared;

public sealed class TestStreamRequest(int count) : StreamRequestBase<int>
{
    public int Count { get; } = count;
}

public sealed class TestStreamRequestHandler : IStreamRequestHandler<TestStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(TestStreamRequest request, CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.Yield();
        }
    }
}
