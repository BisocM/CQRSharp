using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     An inbox over the in-memory one that says whether it joins the unit of work, records when a delivery is recorded
///     in a shared <see cref="TransactionLog" /> (so a test can see whether that was before or after the commit), and
///     misbehaves on demand.
/// </summary>
public sealed class RecordingInboxStore(bool joinsUnitOfWork, TransactionLog log) : IInboxStore
{
    private readonly InMemoryInboxStore _inner = new(new FakeTimeProvider(), Options.Create(new InMemoryOutboxStoreOptions()));

    public bool JoinsUnitOfWork => joinsUnitOfWork;

    /// <summary>Makes the next record report that another delivery recorded first.</summary>
    public bool ReportDuplicateOnNextRecord { get; set; }

    /// <summary>Makes every record throw this until cleared.</summary>
    public Exception? RecordFailure { get; set; }

    /// <summary>Runs first in every record, with the token the record was given; the record waits for it.</summary>
    public Func<CancellationToken, Task>? BeforeRecord { get; set; }

    /// <summary>Makes every delivered-check throw this until cleared.</summary>
    public Exception? CheckFailure { get; set; }

    public Task<bool> IsDeliveredAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
        => CheckFailure is { } failure ? Task.FromException<bool>(failure) : _inner.IsDeliveredAsync(messageId, handlerName, cancellationToken);

    public async Task<bool> RecordDeliveryAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
    {
        if (BeforeRecord is { } before)
            await before(cancellationToken);

        if (RecordFailure is { } failure)
        {
            log.Add("record-failed");
            throw failure;
        }

        if (ReportDuplicateOnNextRecord)
        {
            ReportDuplicateOnNextRecord = false;
            log.Add("record-duplicate");
            return false;
        }

        log.Add("record");
        return await _inner.RecordDeliveryAsync(messageId, handlerName, cancellationToken);
    }
}
