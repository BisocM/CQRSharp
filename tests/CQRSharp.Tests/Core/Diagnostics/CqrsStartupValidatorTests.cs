using CQRSharp.Core.Diagnostics;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Tests for <see cref="CqrsStartupValidator" />: driven directly against a real <see cref="ServiceCollection" />
///     wired with <c>AddCqrsGenerated</c>, and through a real host, where it must run before any hosted service starts.
///     The test assembly declares idempotent and retryable requests, so a configuration is only clean with the
///     idempotency and resilience behaviors wired: <see cref="Build" /> wires them, so each test sees only the issue it
///     seeds.
/// </summary>
public sealed class CqrsStartupValidatorTests
{
    private static CqrsStartupValidator CreateValidator(
        IServiceProvider provider, CqrsValidationPolicy policy, ILogger<CqrsStartupValidator>? logger = null)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CqrsStartupValidationOptions { Policy = policy }),
            logger ?? NullLogger<CqrsStartupValidator>.Instance);

    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(Wired);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static void Wired(ICqrsBuilder builder)
        => builder.UseIdempotency(i => i.UseInMemoryStore()).UseResilience(_ => { });

    // RetryableTestCommand without the resilience behavior: CQRCONF006, a warning, and nothing else.
    private static ServiceProvider WithOnlyAWarning()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseInMemoryStore()));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CQRCONF001_aborts_under_ThrowOnError_when_outbox_unbacked()
    {
        await using var provider = Build(services => services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled));

        var act = () => CreateValidator(provider, CqrsValidationPolicy.ThrowOnError).StartingAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF001");
    }

    [Fact]
    public async Task CQRCONF001_cleared_after_AddInMemoryOutboxStore()
    {
        await using var provider = Build(services =>
        {
            services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled);
            services.AddInMemoryOutboxStore();
        });

        // With the outbox backed by a store, CQRCONF001 does not fire and host start proceeds.
        await CreateValidator(provider, CqrsValidationPolicy.ThrowOnError).StartingAsync(CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>()
            .DescribeConfiguration().Should().NotContain(i => i.Code == "CQRCONF001");
    }

    [Fact(DisplayName = "ThrowOnError ignores warnings")]
    public async Task A_warning_does_not_throw_under_ThrowOnError()
    {
        await using var provider = WithOnlyAWarning();

        await using (var scope = provider.CreateAsyncScope())
            scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeConfiguration()
                .Should().Contain(i => i.Code == "CQRCONF006")
                .And.OnlyContain(i => i.Severity == CqrsBindingIssueSeverity.Warning);

        await CreateValidator(provider, CqrsValidationPolicy.ThrowOnError).StartingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ThrowOnWarning_aborts_on_a_warning()
    {
        await using var provider = WithOnlyAWarning();

        var act = () => CreateValidator(provider, CqrsValidationPolicy.ThrowOnWarning).StartingAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF006");
    }

    [Fact]
    public async Task CQRCONF004_reported_when_the_generated_registrations_are_absent()
    {
        // AddCqrs alone, without the generated registrations: no ICqrsDiagnostics, so the validator records CQRCONF004 directly.
        var services = new ServiceCollection();
        services.AddCqrs();
        await using var provider = services.BuildServiceProvider();

        provider.GetService<ICqrsDiagnostics>().Should().BeNull();

        var act = () => CreateValidator(provider, CqrsValidationPolicy.ThrowOnError).StartingAsync(CancellationToken.None);

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

        await validator.StartingAsync(CancellationToken.None);

        scopeFactory.CreatedScopes.Should().Be(0);
    }

    [Fact(DisplayName = "WarnOnly logs an error it finds and lets host start proceed")]
    public async Task WarnOnly_does_not_throw_even_on_errors()
    {
        await using var provider = Build(services => services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled));
        var logger = new CapturingLogger<CqrsStartupValidator>();

        await CreateValidator(provider, CqrsValidationPolicy.WarnOnly, logger).StartingAsync(CancellationToken.None);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("CQRCONF001"),
            "the error is found and reported, only not thrown");
    }

    [Fact(DisplayName = "A hand-registered handler of a durable notification that cannot be constructed is CQRCONF012 under WarnOnly, not an aborted start")]
    public async Task Unconstructible_hand_registered_handler_is_reported_under_WarnOnly()
    {
        await using var provider = WithUnconstructibleHandler();
        var logger = new CapturingLogger<CqrsStartupValidator>();

        await CreateValidator(provider, CqrsValidationPolicy.WarnOnly, logger).StartingAsync(CancellationToken.None);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("CQRCONF012") && e.Message.Contains("boom-ctor"));
    }

    [Fact(DisplayName = "A hand-registered handler of a durable notification that cannot be constructed fails ThrowOnError with the validation report")]
    public async Task Unconstructible_hand_registered_handler_fails_ThrowOnError_with_the_report()
    {
        await using var provider = WithUnconstructibleHandler();

        var act = () => CreateValidator(provider, CqrsValidationPolicy.ThrowOnError).StartingAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("CQRSharp startup validation failed").And.Contain("CQRCONF012").And.Contain(typeof(FanOutOrderPlaced).FullName!);
    }

    // A durable notification with a handler registered by hand whose factory throws: telling whether it is subscribed
    // means constructing it.
    private static ServiceProvider WithUnconstructibleHandler()
        => Build(services =>
        {
            services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled);
            services.AddInMemoryOutboxStore();
            services.AddTransient<INotificationHandler<FanOutOrderPlaced>>(_ => throw new InvalidOperationException("boom-ctor"));
        });

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
                // Outbox enabled with no store: CQRCONF001 (Error). The validator is opt-in, so it is turned on.
                services.AddCqrsGenerated(b => Wired(b.ValidateOnStart()));
                services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled);
            })
            .Build();

        var act = () => host.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF001");
    }

    [Theory(DisplayName = "Validation runs before any hosted service starts, whatever the registration order")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validation_runs_before_every_hosted_service(bool startConcurrently)
    {
        var started = new StartRecorder();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ServicesStartConcurrently = startConcurrently);
                // Registered before CQRSharp: a seeder or migrator that dispatches as it starts.
                services.AddSingleton(started);
                services.AddHostedService<RecordingHostedService>();
                services.AddCqrsGenerated(b => Wired(b.ValidateOnStart()));
                services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled);
            })
            .Build();

        var act = () => host.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF001");
        started.Started.Should().BeFalse("no hosted service may start against a configuration the validator rejects");
    }

    [Fact(DisplayName = "Startup validation is opt-in: a seeded error does not abort host start without a policy")]
    public async Task Validation_is_opt_in()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddCqrsGenerated();
                services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled);
            })
            .Build();

        await using (var scope = host.Services.CreateAsyncScope())
            scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeConfiguration()
                .Should().Contain(i => i.Code == "CQRCONF001", "the configuration does carry an error for a policy to act on");

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "A clean configuration passes validation under ThrowOnWarning and the host starts")]
    public async Task Host_start_succeeds_when_configuration_is_clean()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddCqrsGenerated(b => Wired(b.ValidateOnStart(CqrsValidationPolicy.ThrowOnWarning))))
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "A queued application that declares stream requests passes validation: its streams run inline")]
    public async Task Queued_application_with_streams_is_clean()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddCqrsGenerated(b => Wired(b
                .ConfigureDispatcher(o => o.RunMode = RunMode.Queued)
                .ValidateOnStart(CqrsValidationPolicy.ThrowOnWarning))))
            .Build();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
            diagnostics.DescribeAllRequests().Should().Contain(b => typeof(IStreamRequest).IsAssignableFrom(b.RequestType),
                "the assembly declares stream requests");
            diagnostics.DescribeConfiguration().Should().BeEmpty();
        }

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "An undefined validation policy fails host start instead of acting as WarnOnly")]
    public async Task Undefined_policy_fails_host_start()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddCqrsGenerated(Wired);
                services.Configure<CqrsStartupValidationOptions>(o => o.Policy = (CqrsValidationPolicy)42);
            })
            .Build();

        var act = () => host.StartAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OptionsValidationException>()).WithMessage("*CqrsValidationPolicy*");
    }

    [Fact(DisplayName = "An idempotent request without the idempotency behavior is an error; a retryable one without resilience is a warning")]
    public async Task CQRCONF005_and_006_surface_through_the_validator_for_unwired_markers()
    {
        // IdempotentTestCommand / RetryableTestCommand are discovered in this assembly, and nothing is wired here.
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var config = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeConfiguration();
            config.Where(i => i.Code == "CQRCONF005").Should().NotBeEmpty().And.OnlyContain(i => i.Severity == CqrsBindingIssueSeverity.Error);
            config.Where(i => i.Code == "CQRCONF006").Should().NotBeEmpty().And.OnlyContain(i => i.Severity == CqrsBindingIssueSeverity.Warning);
        }

        // The error aborts host start under ThrowOnError, the warning only under ThrowOnWarning.
        var onError = () => CreateValidator(provider, CqrsValidationPolicy.ThrowOnError).StartingAsync(CancellationToken.None);
        (await onError.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("CQRCONF005").And.NotContain("CQRCONF006");

        var onWarning = () => CreateValidator(provider, CqrsValidationPolicy.ThrowOnWarning).StartingAsync(CancellationToken.None);
        (await onWarning.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("CQRCONF005").And.Contain("CQRCONF006");
    }

    [Fact]
    public async Task CQRCONF005_and_006_cleared_when_the_behaviors_are_wired()
    {
        await using var provider = Build();

        await using (var scope = provider.CreateAsyncScope())
            scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeConfiguration().Should().BeEmpty();

        // And host start proceeds (no warnings to abort on under ThrowOnWarning).
        await CreateValidator(provider, CqrsValidationPolicy.ThrowOnWarning).StartingAsync(CancellationToken.None);
    }

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        public int CreatedScopes { get; private set; }

        public IServiceScope CreateScope()
        {
            CreatedScopes++;
            throw new InvalidOperationException("Off policy must not create a scope.");
        }
    }

    private sealed class StartRecorder
    {
        public volatile bool Started;
    }

    private sealed class RecordingHostedService(StartRecorder recorder) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            recorder.Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
