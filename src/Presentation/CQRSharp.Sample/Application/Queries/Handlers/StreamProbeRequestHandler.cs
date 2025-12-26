using System.Runtime.CompilerServices;
using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Queries.Handlers;

public sealed class StreamProbeRequestHandler(
    SampleScopedMarker marker,
    ILogger<StreamProbeRequestHandler> logger)
    : IStreamRequestHandler<StreamProbeRequest, StreamProbeItem>
{
    public async IAsyncEnumerable<StreamProbeItem> Handle(StreamProbeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling StreamProbeRequest with Count={Count}", request.Count);

        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new StreamProbeItem(i + 1, marker.Id);
            await Task.Yield();
        }
    }
}
