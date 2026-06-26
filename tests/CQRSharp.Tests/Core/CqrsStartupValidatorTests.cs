using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Tests for <see cref="CqrsStartupValidator" />, driving it against a real <see cref="ServiceCollection" />
///     wired with <c>AddCqrsGenerated</c>, plus a host-start integration test that asserts boot aborts on a seeded
///     error.
/// </summary>
public sealed class CqrsStartupValidatorTests
{
    private static CqrsStartupValidator CreateValidator(IServiceProvider provider, CqrsValidationPolicy policy)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CqrsStartupValidationOptions { Policy = policy }),
            NullLogger<CqrsStartupValidator>.Instance);

    [Fact]
    public async Task CQRCONF001_aborts_under_default_policy_when_outbox_unbacked()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(configureOutbox: o => o.Mode = OutboxMode.Enabled);
        await using var provider = services.BuildServiceProvider();

        var validator = CreateValidator(provider, CqrsValidationPolicy.ThrowOnError);

        var act = () => validator.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF001");
    }

    [Fact]
    public async Task CQRCONF001_cleared_after_AddInMemoryOutboxStore()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(configureOutbox: o => o.Mode = OutboxMode.Enabled);
        services.AddInMemoryOutboxStore();
        RegisterTestRequestFactories(services);
        await using var provider = services.BuildServiceProvider();

        var validator = CreateValidator(provider, CqrsValidationPolicy.ThrowOnError);

        // With the outbox now backed, CQRCONF001 no longer fires and host start proceeds.
        await validator.StartAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>()
            .DescribeConfiguration().Should().NotContain(i => i.Code == "CQRCONF001");
    }

    [Fact]
    public async Task CQRCONF002_warns_but_does_not_throw_under_default_policy()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(configureOutbox: o => o.Mode = OutboxMode.Transactional);
        services.AddInMemoryOutboxStore();
        RegisterTestRequestFactories(services);
        // A plain (non-explicit) IUnitOfWork triggers CQRCONF002 (Warning) but no error.
        services.AddScoped<CQRSharp.Abstractions.Interfaces.Transactions.IUnitOfWork, PlainUnitOfWork>();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
            scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>()
                .DescribeConfiguration().Should().Contain(i => i.Code == "CQRCONF002");

        var validator = CreateValidator(provider, CqrsValidationPolicy.ThrowOnError);

        // ThrowOnError ignores warnings: must not throw.
        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ThrowOnWarning_aborts_on_a_warning()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(configureOutbox: o => o.Mode = OutboxMode.Transactional);
        services.AddInMemoryOutboxStore();
        RegisterTestRequestFactories(services);
        services.AddScoped<CQRSharp.Abstractions.Interfaces.Transactions.IUnitOfWork, PlainUnitOfWork>();
        await using var provider = services.BuildServiceProvider();

        var validator = CreateValidator(provider, CqrsValidationPolicy.ThrowOnWarning);

        var act = () => validator.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF002");
    }

    [Fact]
    public async Task CQRCONF004_reported_when_request_registry_absent()
    {
        // AddCqrs without AddGenerated: no ICqrsDiagnostics, so the validator records CQRCONF004 directly.
        var services = new ServiceCollection();
        services.AddCqrs();
        await using var provider = services.BuildServiceProvider();

        provider.GetService<ICqrsDiagnostics>().Should().BeNull();

        var validator = CreateValidator(provider, CqrsValidationPolicy.ThrowOnError);

        var act = () => validator.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF004");
    }

    [Fact]
    public async Task Off_policy_never_creates_a_scope_and_never_throws()
    {
        var scopeFactory = new CountingScopeFactory();
        var validator = new CqrsStartupValidator(
            scopeFactory,
            Options.Create(new CqrsStartupValidationOptions { Policy = CqrsValidationPolicy.Off }),
            NullLogger<CqrsStartupValidator>.Instance);

        await validator.StartAsync(CancellationToken.None);

        scopeFactory.CreatedScopes.Should().Be(0);
    }

    [Fact]
    public async Task WarnOnly_does_not_throw_even_on_errors()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(configureOutbox: o => o.Mode = OutboxMode.Enabled);
        await using var provider = services.BuildServiceProvider();

        var validator = CreateValidator(provider, CqrsValidationPolicy.WarnOnly);

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public void AddCqrs_twice_registers_exactly_one_startup_validator()
    {
        var services = new ServiceCollection();
        services.AddCqrs();
        services.AddCqrs();

        services.Count(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(CqrsStartupValidator))
            .Should().Be(1);
    }

    [Fact]
    public async Task Host_start_aborts_on_seeded_error()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                // Outbox enabled with no store: CQRCONF001 (Error) under the default ThrowOnError policy.
                services.AddCqrsGenerated(configureOutbox: o => o.Mode = OutboxMode.Enabled);
            })
            .Build();

        var act = () => host.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();

        await host.StopAsync();
    }

    [Fact]
    public async Task Host_start_succeeds_when_configuration_is_clean()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddCqrsGenerated(configureOutbox: o => o.Mode = OutboxMode.Disabled);
                RegisterTestRequestFactories(services);
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    // The shared diagnostics test fixtures include a request with a custom context whose factory is otherwise
    // registered only per-test; register it here so DescribeAllRequests() yields no per-request CQRDIAG errors and
    // the assertions isolate the configuration-level codes under test.
    private static void RegisterTestRequestFactories(IServiceCollection services)
        => services.AddTransient<
            CQRSharp.Core.Factories.IRequestContextFactory<DiagnosticsCustomContext>,
            DiagnosticsCustomContextFactory>();

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        public int CreatedScopes { get; private set; }

        public IServiceScope CreateScope()
        {
            CreatedScopes++;
            throw new InvalidOperationException("Off policy must not create a scope.");
        }
    }

    private sealed class PlainUnitOfWork : CQRSharp.Abstractions.Interfaces.Transactions.IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public TService GetService<TService>() where TService : class => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
