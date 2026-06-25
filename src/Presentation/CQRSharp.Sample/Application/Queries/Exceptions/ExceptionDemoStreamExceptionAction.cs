using CQRSharp.Abstractions.Interfaces.Exceptions;
using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Queries.Exceptions;

public sealed class ExceptionDemoStreamExceptionAction(SampleDiagnostics diagnostics)
    : IRequestExceptionAction<ExceptionDemoStreamRequest, ExceptionDemoStreamException>
{
    public Task Execute(
        ExceptionDemoStreamRequest request,
        ExceptionDemoStreamException exception,
        CancellationToken cancellationToken)
    {
        diagnostics.RecordExceptionAction(typeof(ExceptionDemoStreamRequest));
        return Task.CompletedTask;
    }
}
