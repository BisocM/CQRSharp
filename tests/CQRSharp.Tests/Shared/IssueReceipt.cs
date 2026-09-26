using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Shared;

/// <summary>Counts the receipts <see cref="IssueReceiptHandler" /> issued, so a test can tell a replay from a second run.</summary>
public sealed class ReceiptCounter
{
    private int _issued;
    public int Issued => _issued;
    public int Next() => Interlocked.Increment(ref _issued);
}

/// <summary>A value-returning idempotent command: a retry under the same key is answered with the original receipt.</summary>
public sealed class IssueReceipt : ResultCommandBase<string>, IIdempotentRequest
{
    public required string IdempotencyKey { get; init; }
    public bool Decline { get; init; }
}

public sealed class IssueReceiptHandler(IServiceProvider services) : IResultCommandHandler<IssueReceipt, string>
{
    public Task<CommandResult<string>> Handle(IssueReceipt command, CancellationToken cancellationToken)
    {
        if (command.Decline)
            return Task.FromResult(CommandResult<string>.FromError("declined"));

        // Optional, so the (assembly-wide auto-registered) handler is harmless in every other test's container.
        var number = services.GetService<ReceiptCounter>()?.Next() ?? 0;
        return Task.FromResult(CommandResult<string>.FromSuccess($"receipt-{number}"));
    }
}
