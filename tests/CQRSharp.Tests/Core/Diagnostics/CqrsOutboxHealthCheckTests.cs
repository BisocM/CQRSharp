using CQRSharp.Core.Diagnostics.HealthChecks;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace CQRSharp.Tests.Core;

/// <summary>The outbox health check: healthy, degraded on lag or dead letters, unhealthy when the store cannot be read.</summary>
public sealed class CqrsOutboxHealthCheckTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private static (CqrsOutboxHealthCheck Check, Mock<IOutboxStore> Store) Create(OutboxMode mode = OutboxMode.Enabled, Action<OutboxHealthCheckOptions>? configure = null)
    {
        var store = new Mock<IOutboxStore>(MockBehavior.Strict);
        var services = new ServiceCollection();
        services.AddScoped(_ => store.Object);
        var thresholds = services.AddOptions<OutboxHealthCheckOptions>("cqrsharp.outbox");
        if (configure is not null) thresholds.Configure(configure);
        var provider = services.BuildServiceProvider();

        var check = new CqrsOutboxHealthCheck(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<OutboxHealthCheckOptions>>(),
            Options.Create(new OutboxOptions { Mode = mode }),
            new FakeTimeProvider(new DateTimeOffset(Now)));
        return (check, store);
    }

    private static HealthCheckContext Context(string name = "cqrsharp.outbox")
        => new()
        {
            Registration = new HealthCheckRegistration(name, _ => throw new InvalidOperationException("not used"), HealthStatus.Unhealthy, null)
        };

    [Fact(DisplayName = "A small, fresh backlog is healthy and reported in the data")]
    public async Task Healthy_with_data()
    {
        var (check, store) = Create();
        store.Setup(s => s.GetBacklogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OutboxBacklog(3, 0, Now.AddSeconds(-30)));

        var result = await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["pending"].Should().Be(3L);
        result.Data["deadLetters"].Should().Be(0L);
        result.Data["lagSeconds"].Should().Be(30d);
    }

    [Fact(DisplayName = "An oldest message older than MaxLag is degraded")]
    public async Task Degraded_on_lag()
    {
        var (check, store) = Create(configure: o => o.MaxLag = TimeSpan.FromMinutes(1));
        store.Setup(s => s.GetBacklogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OutboxBacklog(1, 0, Now.AddMinutes(-10)));

        var result = await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("oldest undelivered message");
    }

    [Fact(DisplayName = "Dead letters above MaxDeadLetters are degraded; the default tolerates none")]
    public async Task Degraded_on_dead_letters()
    {
        var (check, store) = Create();
        store.Setup(s => s.GetBacklogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OutboxBacklog(0, 2, null));

        var result = await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("2 dead letter(s)");

        var (tolerant, tolerantStore) = Create(configure: o => o.MaxDeadLetters = 5);
        tolerantStore.Setup(s => s.GetBacklogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OutboxBacklog(0, 2, null));
        (await tolerant.CheckHealthAsync(Context(), TestContext.Current.CancellationToken)).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact(DisplayName = "A store that throws yields the registration's failure status")]
    public async Task Unhealthy_when_the_store_fails()
    {
        var (check, store) = Create();
        store.Setup(s => s.GetBacklogAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        var result = await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact(DisplayName = "A disabled outbox is healthy without touching a store")]
    public async Task Healthy_when_disabled()
    {
        var (check, _) = Create(OutboxMode.Disabled);

        var result = await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("disabled");
    }

    [Fact(DisplayName = "AddCqrsOutbox registers the check with its thresholds, named after it")]
    public void Registration()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        services.AddHealthChecks().AddCqrsOutbox(configure: o => o.MaxLag = TimeSpan.FromSeconds(1));
        using var provider = services.BuildServiceProvider();

        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Should().ContainSingle(r => r.Name == "cqrsharp.outbox");
        provider.GetRequiredService<IOptionsMonitor<OutboxHealthCheckOptions>>().Get("cqrsharp.outbox").MaxLag.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact(DisplayName = "Two outbox checks keep their own thresholds: each degrades at its own lag")]
    public async Task Two_registrations_keep_their_own_thresholds()
    {
        var store = new Mock<IOutboxStore>();
        store.Setup(s => s.GetBacklogAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OutboxBacklog(1, 0, Now.AddMinutes(-10)));
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        services.AddSingleton(store.Object);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(Now)));
        services.AddHealthChecks()
            .AddCqrsOutbox("outbox-live", tags: ["live"], configure: o => o.MaxLag = TimeSpan.FromHours(1))
            .AddCqrsOutbox("outbox-ready", tags: ["ready"], configure: o => o.MaxLag = TimeSpan.FromMinutes(2));
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        report.Entries["outbox-live"].Status.Should().Be(HealthStatus.Healthy, "ten minutes is within the liveness check's hour");
        report.Entries["outbox-ready"].Status.Should().Be(HealthStatus.Degraded, "ten minutes is past the readiness check's two");
    }
}
