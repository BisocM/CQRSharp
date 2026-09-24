using System.Runtime.CompilerServices;
using CQRSharp.Sample.Application.Queries.Requests;

namespace CQRSharp.Sample.Application.Queries.Handlers;

public sealed class CountdownStreamRequestHandler : IStreamRequestHandler<CountdownStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(CountdownStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = request.From; i > 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.Yield();
        }
    }
}
