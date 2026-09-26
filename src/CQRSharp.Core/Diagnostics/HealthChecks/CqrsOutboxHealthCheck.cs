using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Diagnostics.HealthChecks;

/// <summary>
///     Thresholds for <see cref="CqrsOutboxHealthCheck" />: named options, one set per health-check registration, named
///     after it.
/// </summary>
public sealed class OutboxHealthCheckOptions
{
    /// <summary>
    ///     The oldest an undelivered message may be before the outbox is reported degraded: the processor is behind, down,
    ///     or a partition head is stuck retrying. Defaults to 5 minutes; <see langword="null" /> never degrades on lag.
    /// </summary>
    public TimeSpan? MaxLag { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     The number of dead letters above which the outbox is reported degraded. Defaults to 0: any dead letter needs an
    ///     operator's attention. <see langword="null" /> never degrades on dead letters.
    /// </summary>
    public long? MaxDeadLetters { get; set; } = 0;
}

/// <summary>
///     Reports the outbox's health from a live <see cref="IOutboxStore.GetBacklogAsync" />: unhealthy when the store
///     cannot be reached, degraded when the lag or the dead-letter count exceeds the configured thresholds, healthy
///     otherwise (and healthy, with a note, when the outbox is disabled). The measured backlog is in the result's data.
/// </summary>
public sealed class CqrsOutboxHealthCheck(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<OutboxHealthCheckOptions> options,
    IOptions<OutboxOptions> outboxOptions,
    TimeProvider? timeProvider = null) : IHealthCheck
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (outboxOptions.Value.Mode == OutboxMode.Disabled)
            return HealthCheckResult.Healthy("The outbox is disabled.");

        OutboxBacklog backlog;
        try
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var store = scope.ServiceProvider.GetService<IOutboxStore>();
                if (store is null)
                    return new HealthCheckResult(context.Registration.FailureStatus, "The outbox is enabled but no IOutboxStore is registered.");

                backlog = await store.GetBacklogAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "The outbox store could not be read.", ex);
        }

        var lag = backlog.LagAt(_timeProvider.GetUtcNow().UtcDateTime);
        var data = new Dictionary<string, object>
        {
            ["pending"] = backlog.PendingCount,
            ["deadLetters"] = backlog.DeadLetterCount,
            ["lagSeconds"] = Math.Round(lag.TotalSeconds, 3)
        };
        if (backlog.OldestPendingCreatedAt is { } oldest)
            data["oldestPendingCreatedAt"] = oldest;

        // Each registration's own thresholds: the options named after it (AddCqrsOutbox configures them so).
        var thresholds = options.Get(context.Registration.Name);
        var reasons = new List<string>(2);
        if (thresholds.MaxLag is { } maxLag && lag > maxLag)
            reasons.Add($"the oldest undelivered message is {FormatAge(lag)} old (limit {FormatAge(maxLag)})");
        if (thresholds.MaxDeadLetters is { } maxDead && backlog.DeadLetterCount > maxDead)
            reasons.Add($"{backlog.DeadLetterCount} dead letter(s) (limit {maxDead})");

        return reasons.Count > 0
            ? HealthCheckResult.Degraded($"Outbox needs attention: {string.Join("; ", reasons)}.", data: data)
            : HealthCheckResult.Healthy($"Outbox OK ({backlog.PendingCount} pending, {backlog.DeadLetterCount} dead letter(s)).", data);
    }

    private static string FormatAge(TimeSpan age)
        => age.TotalSeconds < 120 ? $"{age.TotalSeconds:0}s" : age.TotalMinutes < 120 ? $"{age.TotalMinutes:0}m" : $"{age.TotalHours:0.#}h";
}
