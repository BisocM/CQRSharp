using CQRSharp.Abstractions.Interfaces.Exceptions;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Abstractions.Models.Exceptions;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Commands.Exceptions;

public sealed class ExceptionDemoExceptionHandler(SampleDiagnostics diagnostics)
    : IRequestExceptionHandler<ExceptionDemoCommand, CommandResult, ExceptionDemoException>
{
    public Task Handle(
        ExceptionDemoCommand request,
        ExceptionDemoException exception,
        RequestExceptionHandlerState<CommandResult> state,
        CancellationToken cancellationToken)
    {
        diagnostics.RecordExceptionHandler(typeof(ExceptionDemoCommand));
        state.SetHandled(CommandResult.FromError("Exception handled."));
        return Task.CompletedTask;
    }
}

