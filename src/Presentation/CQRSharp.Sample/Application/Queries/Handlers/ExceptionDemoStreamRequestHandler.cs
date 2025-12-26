using System.Runtime.CompilerServices;
using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Sample.Application.Queries.Exceptions;
using CQRSharp.Sample.Application.Queries.Requests;

namespace CQRSharp.Sample.Application.Queries.Handlers;

public sealed class ExceptionDemoStreamRequestHandler : IStreamRequestHandler<ExceptionDemoStreamRequest, ExceptionDemoStreamItem>
{
    public async IAsyncEnumerable<ExceptionDemoStreamItem> Handle(ExceptionDemoStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (cancellationToken.IsCancellationRequested) yield break;
        throw new ExceptionDemoStreamException();
    }
}
