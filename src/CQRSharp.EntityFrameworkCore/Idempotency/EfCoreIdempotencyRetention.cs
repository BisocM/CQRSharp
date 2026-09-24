using System.Diagnostics.CodeAnalysis;
using CQRSharp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The EF Core idempotency store's retention: deletes the keys whose claim expired, when the host starts and then
///     once per <see cref="EfCoreIdempotencyStoreOptions.Retention" /> (at least hourly). An expired row is otherwise only
///     reused by a claim of the same key, so with a key per request the table would grow without bound.
/// </summary>
/// <typeparam name="TContext">The context that maps the idempotency table.</typeparam>
internal sealed class EfCoreIdempotencyRetention<TContext>(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<EfCoreIdempotencyStoreOptions> options,
    EfCoreStoreSelection selection,
    ILogger<EfCoreIdempotencyRetention<TContext>> logger) : RetentionService(timeProvider)
    where TContext : DbContext
{
    private static readonly TimeSpan LongestInterval = TimeSpan.FromHours(1);

    // A short retention purged hourly would let the table hold many windows' worth of expired keys.
    private readonly TimeSpan _interval = options.Value.Retention < LongestInterval ? options.Value.Retention : LongestInterval;

    /// <inheritdoc />
    protected override TimeSpan Interval => _interval;

    /// <inheritdoc />
    protected override bool IsInUse() => selection.Selects<IIdempotencyStore, EfCoreIdempotencyStore<TContext>>();

    /// <inheritdoc />
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    internal override async Task PurgeAsync(CancellationToken cancellationToken)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var expired = scope.ServiceProvider.GetRequiredService<TContext>().Set<IdempotencyEntity>()
                .Where(e => e.ExpiresAt <= now)
                .OrderBy(e => e.ExpiresAt);

            var deleted = await PagedDelete.RunAsync(expired, cancellationToken).ConfigureAwait(false);
            if (deleted > 0) EfCoreLog.ExpiredKeysPurged(logger, deleted);
        }
    }

    /// <inheritdoc />
    protected override void LogPurgeFailed(Exception exception, TimeSpan retryIn) => EfCoreLog.IdempotencyPurgeFailed(logger, exception, retryIn);
}
