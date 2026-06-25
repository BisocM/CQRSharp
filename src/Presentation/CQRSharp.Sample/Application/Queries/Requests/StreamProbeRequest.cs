using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Queries.Requests;

public sealed class StreamProbeRequest(int count) : StreamRequestBase<StreamProbeItem, SampleRequestContext>
{
    public int Count { get; } = count;
}

public sealed record StreamProbeItem(int Value, Guid ScopedMarkerId);
