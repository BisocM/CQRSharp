using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Queries.Requests;

/// <summary>A stream of value-type items: the streaming counterpart of <see cref="AddNumbersQuery" /> for Native AOT.</summary>
public sealed class CountdownStreamRequest(int from) : StreamRequestBase<int, SampleRequestContext>
{
    public int From { get; } = from;
}
