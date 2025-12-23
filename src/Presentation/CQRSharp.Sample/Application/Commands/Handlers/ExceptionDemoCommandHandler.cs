using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Sample.Application.Commands.Exceptions;
using CQRSharp.Sample.Application.Commands.Requests;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class ExceptionDemoCommandHandler : ICommandHandler<ExceptionDemoCommand>
{
    public Task<CommandResult> Handle(ExceptionDemoCommand command, CancellationToken cancellationToken)
        => throw new ExceptionDemoException();
}

