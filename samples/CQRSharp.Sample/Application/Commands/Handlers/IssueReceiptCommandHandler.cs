using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Domain.Entities;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class IssueReceiptCommandHandler(SampleDiagnostics diagnostics)
    : IResultCommandHandler<IssueReceiptCommand, Receipt>
{
    public Task<CommandResult<Receipt>> Handle(IssueReceiptCommand command, CancellationToken cancellationToken)
    {
        diagnostics.CountRun(command.IdempotencyKey);
        return Task.FromResult(CommandResult<Receipt>.FromSuccess(new Receipt(Guid.NewGuid(), command.Amount)));
    }
}
