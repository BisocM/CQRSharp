using System.Runtime.CompilerServices;
using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Queries.Handlers;

public sealed class StreamProbeRequestHandler(SampleScopedMarker marker) : IStreamRequestHandler<StreamProbeRequest, StreamProbeItem>
{
    public async IAsyncEnumerable<StreamProbeItem> Handle(StreamProbeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new StreamProbeItem(i + 1, marker.Id);
            await Task.Yield();
        }
    }
}
