using System.Diagnostics.CodeAnalysis;
using CQRSharp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The EF Core outbox store's retention: deletes processed messages past
///     <see cref="EfCoreOutboxStoreOptions.ProcessedRetention" />, dead letters past
///     <see cref="EfCoreOutboxStoreOptions.DeadLetterRetention" /> and inbox records past
///     <see cref="EfCoreOutboxStoreOptions.InboxRetention" />, when the host starts and then every
///     <see cref="EfCoreOutboxStoreOptions.PurgeInterval" />. Idle while the outbox is off, like the outbox processor.
/// </summary>
/// <typeparam name="TContext">The context that maps the outbox and inbox tables.</typeparam>
internal sealed class EfCoreOutboxRetention<TContext>(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<EfCoreOutboxStoreOptions> storeOptions,
    IOptions<OutboxOptions> outboxOptions,
    EfCoreStoreSelection selection,
    ILogger<EfCoreOutboxRetention<TContext>> logger) : RetentionService(timeProvider)
    where TContext : DbContext
{
    private readonly EfCoreOutboxStoreOptions _options = storeOptions.Value;

    /// <inheritdoc />
    protected override TimeSpan Interval => _options.PurgeInterval;

    /// <inheritdoc />
    protected override bool IsInUse()
        => outboxOptions.Value.Mode != OutboxMode.Disabled && selection.Selects<IOutboxStore, EfCoreOutboxStore<TContext>>();

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    internal override async Task PurgeAsync(CancellationToken cancellationToken)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = scope.ServiceProvider.GetRequiredService<TContext>();

            if (_options.ProcessedRetention is { } processedRetention)
            {
                var deleted = await PagedDelete.RunAsync(OutboxRetentionRules.ProcessedBy(context, now - processedRetention), cancellationToken).ConfigureAwait(false);
                if (deleted > 0) EfCoreLog.ProcessedMessagesPurged(logger, deleted, processedRetention);
            }

            if (_options.DeadLetterRetention is { } deadLetterRetention)
            {
                var deleted = await PagedDelete.RunAsync(OutboxRetentionRules.DeadLettersFailedBy(context, now - deadLetterRetention), cancellationToken).ConfigureAwait(false);
                if (deleted > 0) EfCoreLog.DeadLettersPurged(logger, deleted, deadLetterRetention);
            }

            var inboxDeleted = await PagedDelete.RunAsync(OutboxRetentionRules.InboxRecordsBy(context, now - _options.InboxRetention), cancellationToken).ConfigureAwait(false);
            if (inboxDeleted > 0) EfCoreLog.InboxRecordsPurged(logger, inboxDeleted, _options.InboxRetention);
        }
    }

    /// <inheritdoc />
    protected override void LogPurgeFailed(Exception exception, TimeSpan retryIn) => EfCoreLog.OutboxPurgeFailed(logger, exception, retryIn);
}
