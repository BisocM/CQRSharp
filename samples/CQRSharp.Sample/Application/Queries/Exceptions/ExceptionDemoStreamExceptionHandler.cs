using CQRSharp.Abstractions.Interfaces.Exceptions;
using CQRSharp.Abstractions.Models.Exceptions;
using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Queries.Exceptions;

public sealed class ExceptionDemoStreamExceptionHandler(SampleDiagnostics diagnostics)
    : IRequestExceptionHandler<ExceptionDemoStreamRequest, IAsyncEnumerable<ExceptionDemoStreamItem>, ExceptionDemoStreamException>
{
    public Task Handle(
        ExceptionDemoStreamRequest request,
        ExceptionDemoStreamException exception,
        RequestExceptionHandlerState<IAsyncEnumerable<ExceptionDemoStreamItem>> state,
        CancellationToken cancellationToken)
    {
        diagnostics.RecordExceptionHandler(typeof(ExceptionDemoStreamRequest));
        state.SetHandled(Fallback());
        return Task.CompletedTask;

        static async IAsyncEnumerable<ExceptionDemoStreamItem> Fallback()
        {
            yield return new ExceptionDemoStreamItem(-1);
            yield return new ExceptionDemoStreamItem(-2);
            await Task.Yield();
        }
    }
}