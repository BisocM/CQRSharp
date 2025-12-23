using CQRSharp.Abstractions.Data.Interfaces.Exceptions;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Commands.Exceptions;

public sealed class ExceptionDemoExceptionAction(SampleDiagnostics diagnostics)
    : IRequestExceptionAction<ExceptionDemoCommand, ExceptionDemoException>
{
    public Task Execute(
        ExceptionDemoCommand request,
        ExceptionDemoException exception,
        CancellationToken cancellationToken)
    {
        diagnostics.RecordExceptionAction(typeof(ExceptionDemoCommand));
        return Task.CompletedTask;
    }
}

