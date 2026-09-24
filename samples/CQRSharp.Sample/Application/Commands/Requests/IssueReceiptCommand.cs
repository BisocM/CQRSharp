using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Domain.Entities;

namespace CQRSharp.Sample.Application.Commands.Requests;

/// <summary>
///     An idempotent command: a retry under the same key is answered with the original receipt, replayed from the
///     idempotency store (UseIdempotency(...).ReplayResultsWith(...) in Program.cs), and the handler does not run again.
/// </summary>
public sealed class IssueReceiptCommand(string idempotencyKey, decimal amount)
    : ResultCommandBase<Receipt, SampleRequestContext>, IIdempotentRequest
{
    public string IdempotencyKey { get; } = idempotencyKey;
    public decimal Amount { get; } = amount;
}
