using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     What the outbox processor's end-to-end tests share: the processor out of a built container, publishing through
///     the dispatcher, the in-memory store's contents, and the waits. The processor runs on the thread pool; a test
///     learns what it did from its claims (<see cref="OutboxDrain" />), never from the passing of real time, and moves
///     the fake clock itself when a retry has to come due.
/// </summary>
internal static class OutboxTestHarness
{
    /// <summary>
    ///     A container with the outbox on, whose <see cref="ParallelProbe" /> notifications reach the
    ///     <see cref="DeliveryProbe" />, on a fake clock that starts at a fixed instant, observed by an
    ///     <see cref="OutboxDrain" />. Retries back off without jitter, so their due times are exact. The store is the
    ///     in-memory one unless the test brings its own outbox or inbox; an outbox of its own comes without an inbox
    ///     unless it brings one too.
    /// </summary>
    public static (ServiceProvider Provider, FakeTimeProvider Time, DeliveryProbe Probe) BuildProbed(
        Action<OutboxProcessorOptions>? configure = null,
        IInboxStore? inbox = null,
        Action<ICqrsBuilder>? configureBuilder = null,
        Func<TimeProvider, IOutboxStore>? outbox = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<DeliveryProbe>();
        configureServices?.Invoke(services);
        services.AddCqrsGenerated(b =>
        {
            b.UseOutbox(o =>
            {
                o.Enabled().ConfigureProcessor(p =>
                {
                    p.Retry.JitterFactor = 0;
                    configure?.Invoke(p);
                });

                if (inbox is null && outbox is null)
                    o.UseInMemoryStore();
                else
                    o.UseStore(s =>
                    {
                        if (outbox is null)
                            s.AddInMemoryOutboxStore();
                        else
                            s.AddSingleton(outbox(time));

                        if (inbox is not null)
                        {
                            s.RemoveAll<IInboxStore>();
                            s.AddSingleton(inbox);
                        }
                    });
            });
            configureBuilder?.Invoke(b);
        });
        OutboxDrain.Observe(services);
        var provider = services.BuildServiceProvider();
        return (provider, time, provider.GetRequiredService<DeliveryProbe>());
    }

    /// <summary>The container's outbox processor, not started.</summary>
    public static OutboxProcessor Processor(IServiceProvider provider)
        => provider.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();

    /// <summary>Publishes the notifications in a scope of their own, outside any request.</summary>
    public static async Task PublishAsync(IServiceProvider provider, params INotification[] notifications)
    {
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        foreach (var notification in notifications)
            await dispatcher.Publish(notification);
    }

    /// <summary>What the container's in-memory outbox store holds; processed messages are evicted from it.</summary>
    public static IReadOnlyCollection<OutboxMessage> Stored(IServiceProvider provider)
        => ((InMemoryOutboxStore)OutboxDrain.Unwrap(provider.GetRequiredService<IOutboxStore>())).Snapshot();

    /// <summary>
    ///     Wakes the processor through its signal, without moving the clock, and completes once a claim that began after
    ///     the call came back empty: everything due at the current instant has been delivered and its outcome recorded.
    /// </summary>
    public static Task DrainAsync(IServiceProvider provider)
    {
        var drained = provider.GetRequiredService<OutboxDrain>().NextEmptyClaimAsync();
        provider.GetRequiredService<IOutboxSignal>().Signal();
        return drained.WaitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     Completes once <paramref name="count" /> of the processor's claims in all came back empty, without waking it:
    ///     for a test that proves something else woke it.
    /// </summary>
    public static Task EmptyClaimsAsync(IServiceProvider provider, int count)
        => provider.GetRequiredService<OutboxDrain>().EmptyClaimsAsync(count).WaitAsync(TestContext.Current.CancellationToken);

    /// <summary>
    ///     Drains, then moves the clock to the earliest retry among the in-memory store's pending messages and drains
    ///     again, until none is pending: every message ends processed or dead-lettered, each retry run exactly when due.
    ///     A pending message without a retry of its own waits behind its partition's head.
    /// </summary>
    public static Task SettleAsync(IServiceProvider provider, FakeTimeProvider time) => SettleAsync(provider, time, () => Stored(provider));

    /// <summary>
    ///     <see cref="SettleAsync(IServiceProvider, FakeTimeProvider)" /> for a container whose outbox store keeps its
    ///     messages elsewhere than the in-memory store: <paramref name="stored" /> reads them.
    /// </summary>
    public static async Task SettleAsync(IServiceProvider provider, FakeTimeProvider time, Func<IReadOnlyCollection<OutboxMessage>> stored)
    {
        while (true)
        {
            await DrainAsync(provider);
            var pending = stored().Where(m => m.Status == OutboxMessageStatus.Pending).ToList();
            if (pending.Count == 0) return;

            var now = time.GetUtcNow().UtcDateTime;
            var retries = pending.Where(m => m.NextRetryAt > now).Select(m => m.NextRetryAt!.Value).ToList();
            if (retries.Count == 0)
                throw new InvalidOperationException($"The processor drained with {pending.Count} message(s) pending and no retry to wait for.");

            time.Advance(retries.Min() - now);
        }
    }
}
