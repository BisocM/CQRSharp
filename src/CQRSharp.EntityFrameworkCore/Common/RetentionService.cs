using Microsoft.Extensions.Hosting;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     A store's retention housekeeping as a hosted service of its own: one purge when the host starts, then one every
///     <see cref="Interval" />, each in a scope of its own and in bounded pages. Housekeeping stays off every hot path - no
///     request waits for it, no claim queues behind it, and no caller's cancellation cuts it short - and a purge that
///     fails is logged and tried again an interval later, never surfaced.
/// </summary>
internal abstract class RetentionService(TimeProvider timeProvider) : BackgroundService
{
    /// <summary>The clock the purges measure retention by and wait on.</summary>
    protected TimeProvider Clock { get; } = timeProvider;

    /// <summary>The time between two purges.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>
    ///     Whether this service's store is the one in use; checked before every purge, and the service ends once it is
    ///     not. The store verbs register the service with the store, but a later store registration replaces the store and
    ///     leaves the service behind, which must then leave alone tables no store uses.
    /// </summary>
    protected abstract bool IsInUse();

    /// <summary>Deletes everything past its retention, in bounded pages; throws what the database throws.</summary>
    internal abstract Task PurgeAsync(CancellationToken cancellationToken);

    /// <summary>Reports a purge that failed; it is tried again after <paramref name="retryIn" />.</summary>
    protected abstract void LogPurgeFailed(Exception exception, TimeSpan retryIn);

    /// <inheritdoc />
    /// <remarks>
    ///     Runs on the thread pool: the host starts its hosted services one after another, and the first purge (a scope,
    ///     a context, compiling its queries) must not hold up the ones after it.
    /// </remarks>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => RunAsync(stoppingToken), CancellationToken.None);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    if (!IsInUse()) return;
                    await PurgeAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && stoppingToken.IsCancellationRequested))
                {
                    LogPurgeFailed(ex, Interval);
                }

                await Task.Delay(Interval, Clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping. A page being deleted is rolled back with its statement, and deleted by a later purge.
        }
    }
}
