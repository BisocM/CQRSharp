using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Pipelines;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Verifies the fluent <see cref="ICqrsBuilder" />: which behaviors each verb registers and where the built-ins sit in
///     the pipeline, that the order of different verbs never changes the result, that repeated verbs and repeated builder
///     calls compose without registering anything twice, that <c>UseTimeProvider</c> is authoritative regardless of
///     position, and that the configured dispatcher works.
/// </summary>
public sealed class CqrsBuilderTests
{
    // Two deliberately different verb orderings of the SAME configuration. If the builder is order-insensitive, both
    // must produce identical registrations and identical pipelines.
    private static void ConfigureOrderA(ICqrsBuilder b) => b
        .ConfigureQueue(o => o.Capacity = 64)
        .UseLogging()
        .UseRateLimiting(o =>
        {
            o.MaxTokens = 5;
            o.ReplenishRatePerSecond = 5;
            o.Scope = RateLimitScope.Global;
        })
        .UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(5))
        .UseResilience(o => o.MaxRetries = 2)
        .ValidateOnStart();

    private static void ConfigureOrderB(ICqrsBuilder b) => b
        .ValidateOnStart()
        .UseResilience(o => o.MaxRetries = 2)
        .UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(5))
        .UseRateLimiting(o =>
        {
            o.MaxTokens = 5;
            o.ReplenishRatePerSecond = 5;
            o.Scope = RateLimitScope.Global;
        })
        .UseLogging()
        .ConfigureQueue(o => o.Capacity = 64);

    private static IReadOnlyList<(string? Service, string? Impl, ServiceLifetime Lifetime)> DescriptorSignature(
        IServiceCollection services)
        => services
            .Select(d => (Service: d.ServiceType.FullName, Impl: d.ImplementationType?.FullName, d.Lifetime))
            .OrderBy(x => x.Service, StringComparer.Ordinal)
            .ThenBy(x => x.Impl, StringComparer.Ordinal)
            .ThenBy(x => x.Lifetime)
            .ToArray();

    private static IReadOnlyList<(Type BehaviorType, int Priority)> ResolvedPipeline(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
        return diagnostics.DescribeRequest(typeof(TestCommand)).Pipeline
            .Select(b => (b.BehaviorType, b.Priority))
            .ToArray();
    }

    // The behaviors a builder verb registers without a store or a unit of work, under both pipeline kinds.
    private static readonly (Type Request, Type Stream)[] Behaviors =
    [
        (typeof(ExceptionHandlingBehavior<,>), typeof(StreamExceptionHandlingBehavior<,>)),
        (typeof(ValidationBehavior<,>), typeof(StreamValidationBehavior<,>)),
        (typeof(LoggingBehavior<,>), typeof(StreamLoggingBehavior<,>)),
        (typeof(RateLimitingBehavior<,>), typeof(StreamRateLimitingBehavior<,>)),
        (typeof(TimeoutBehavior<,>), typeof(StreamTimeoutBehavior<,>)),
        (typeof(ResilienceBehavior<,>), typeof(StreamResilienceBehavior<,>))
    ];

    private static int Registrations(IServiceCollection services, Type serviceType, Type implementationType)
        => services.Count(d => d.ServiceType == serviceType && d.ImplementationType == implementationType);

    private static bool Registers(IServiceCollection services, Type behavior)
        => Registrations(services, typeof(IPipelineBehavior<,>), behavior) > 0;

    // The pipeline the diagnostics describe for a request, as open behavior types in execution order (outermost first).
    private static IReadOnlyList<Type> BehaviorOrder(ICqrsDiagnostics diagnostics, Type requestType)
    {
        var binding = diagnostics.DescribeRequest(requestType);
        binding.Issues.Should().BeEmpty("every behavior of {0} resolves", requestType.Name);
        return binding.Pipeline.Select(b => b.BehaviorType.GetGenericTypeDefinition()).ToArray();
    }

    [Fact]
    public void Verb_order_does_not_change_registrations_or_pipeline()
    {
        var servicesA = new ServiceCollection();
        servicesA.AddCqrsGenerated(ConfigureOrderA);

        var servicesB = new ServiceCollection();
        servicesB.AddCqrsGenerated(ConfigureOrderB);

        // Same registrations (by service type / implementation type / lifetime), independent of verb order.
        DescriptorSignature(servicesA).Should().BeEquivalentTo(DescriptorSignature(servicesB));

        using var providerA = servicesA.BuildServiceProvider();
        using var providerB = servicesB.BuildServiceProvider();

        // Same resolved pipeline (behavior types in the same execution order).
        ResolvedPipeline(providerA).Should().Equal(ResolvedPipeline(providerB));
    }

    [Fact]
    public async Task Verb_order_yields_a_working_dispatcher_both_ways()
    {
        foreach (var configure in new Action<ICqrsBuilder>[] { ConfigureOrderA, ConfigureOrderB })
        {
            var services = new ServiceCollection();
            services.AddCqrsGenerated(configure);

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            var commandResult = await cqrs.Send(new TestCommand(), TestContext.Current.CancellationToken);
            commandResult.IsSuccess.Should().BeTrue();

            var queryResult = await cqrs.Send(new TestQuery(), TestContext.Current.CancellationToken);
            queryResult.Value.Should().Be("Success");
        }
    }

    [Fact(DisplayName = "The built-in behaviors run in the documented order, for requests and for streams")]
    public async Task Built_in_behaviors_run_in_the_documented_order()
    {
        // Every behavior on (validation and exception handling by default), the verbs in reverse of the pipeline order.
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(5))
            .UseUnitOfWork(_ => Mock.Of<IUnitOfWork>())
            .UseIdempotency()
            .UseResilience(o => o.MaxRetries = 1)
            .UseLogging()
            .UseRateLimiting(o => o.MaxTokens = 10));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();

        BehaviorOrder(diagnostics, typeof(TestCommand)).Should().Equal(
            typeof(ExceptionHandlingBehavior<,>),
            typeof(RateLimitingBehavior<,>),
            typeof(LoggingBehavior<,>),
            typeof(ValidationBehavior<,>),
            typeof(ResilienceBehavior<,>),
            typeof(IdempotencyBehavior<,>),
            typeof(UnitOfWorkBehavior<,>),
            typeof(TimeoutBehavior<,>));

        BehaviorOrder(diagnostics, typeof(TestStreamRequest)).Should().Equal(
            typeof(StreamExceptionHandlingBehavior<,>),
            typeof(StreamRateLimitingBehavior<,>),
            typeof(StreamLoggingBehavior<,>),
            typeof(StreamValidationBehavior<,>),
            typeof(StreamResilienceBehavior<,>),
            typeof(StreamIdempotencyBehavior<,>),
            typeof(StreamUnitOfWorkBehavior<,>),
            typeof(StreamTimeoutBehavior<,>));
    }

    [Fact(DisplayName = "Validation and exception handling are registered by default; every other behavior only by its own verb")]
    public void Only_validation_and_exception_handling_are_on_by_default()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseLogging());

        Registers(services, typeof(LoggingBehavior<,>)).Should().BeTrue();
        Registers(services, typeof(ValidationBehavior<,>)).Should().BeTrue();
        Registers(services, typeof(ExceptionHandlingBehavior<,>)).Should().BeTrue();
        Registers(services, typeof(TimeoutBehavior<,>)).Should().BeFalse();
        Registers(services, typeof(ResilienceBehavior<,>)).Should().BeFalse();
        Registers(services, typeof(RateLimitingBehavior<,>)).Should().BeFalse();
    }

    [Fact(DisplayName = "UseValidation(false) and UseExceptionHandling(false) keep those behaviors out, whatever else is enabled")]
    public void Opt_outs_are_independent_of_other_verbs()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(5))
            .UseLogging()
            .UseValidation(false)
            .UseExceptionHandling(false));

        Registers(services, typeof(TimeoutBehavior<,>)).Should().BeTrue();
        Registers(services, typeof(LoggingBehavior<,>)).Should().BeTrue();
        Registers(services, typeof(ValidationBehavior<,>)).Should().BeFalse();
        Registers(services, typeof(ExceptionHandlingBehavior<,>)).Should().BeFalse();
    }

    [Fact(DisplayName = "The parameterless AddCqrsGenerated() is the builder with nothing configured: exactly the default validation and exception-handling behaviors")]
    public void Parameterless_overload_registers_the_builder_defaults()
    {
        var parameterless = new ServiceCollection();
        parameterless.AddCqrsGenerated();
        var emptyBuilder = new ServiceCollection();
        emptyBuilder.AddCqrsGenerated(_ => { });

        Behaviors(parameterless).Should().BeEquivalentTo(
            [typeof(ExceptionHandlingBehavior<,>), typeof(ValidationBehavior<,>), typeof(StreamExceptionHandlingBehavior<,>), typeof(StreamValidationBehavior<,>)]);
        Behaviors(parameterless).Should().BeEquivalentTo(Behaviors(emptyBuilder));

        static IEnumerable<Type?> Behaviors(IServiceCollection services)
            => services.Where(d => d.ServiceType == typeof(IPipelineBehavior<,>) || d.ServiceType == typeof(IStreamPipelineBehavior<,>))
                .Select(d => d.ImplementationType);
    }

    [Fact(DisplayName = "A repeated switch takes the value of its last call")]
    public void Repeated_switch_last_call_wins()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false).UseValidation());

        Registers(services, typeof(ValidationBehavior<,>)).Should().BeTrue();
    }

    [Fact(DisplayName = "Repeated Configure* verbs compose: every delegate runs, in call order")]
    public async Task Repeated_configure_verbs_compose()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .ConfigureDispatcher(o => o.RunMode = RunMode.Queued)
            .ConfigureDispatcher(o => o.ScopeMode = ExecutionScopeMode.New)
            .ConfigureQueue(o => o.Capacity = 10)
            .ConfigureQueue(o => o.Capacity *= 2)
            .UseResilience(o => o.MaxRetries = 5)
            .UseResilience(o => o.BaseDelay = TimeSpan.Zero));

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IOptions<DispatcherOptions>>().Value;
        dispatcher.RunMode.Should().Be(RunMode.Queued);
        dispatcher.ScopeMode.Should().Be(ExecutionScopeMode.New);
        provider.GetRequiredService<IOptions<BackgroundTaskQueueOptions>>().Value.Capacity.Should().Be(20);
        var resilience = provider.GetRequiredService<IOptions<ResilienceOptions>>().Value;
        resilience.MaxRetries.Should().Be(5);
        resilience.BaseDelay.Should().Be(TimeSpan.Zero);
    }

    [Fact(DisplayName = "A bare UseIdempotency() keeps the store an earlier UseIdempotency call chose")]
    public async Task Bare_UseIdempotency_keeps_an_earlier_store_choice()
    {
        var store = Mock.Of<IIdempotencyStore>();
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseIdempotency(i => i.UseStore(s => s.AddSingleton(store)))
            .UseIdempotency());

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IIdempotencyStore>().Should().BeSameAs(store);
    }

    [Fact(DisplayName = "A later UseOutbox call that chooses no store keeps the store an earlier one chose")]
    public async Task Later_UseOutbox_keeps_an_earlier_store_choice()
    {
        var store = Mock.Of<IOutboxStore>();
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseOutbox(o => o.UseStore(s => s.AddSingleton(store)))
            .UseOutbox(o => o.Enabled()));

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOutboxStore>().Should().BeSameAs(store);
        provider.GetRequiredService<IOptions<OutboxOptions>>().Value.Mode.Should().Be(OutboxMode.Enabled);
    }

    [Fact(DisplayName = "A second AddCqrsGenerated(builder) call adds its verbs to the first; every behavior is registered once")]
    public async Task Second_builder_call_adds_its_behaviors()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseLogging());
        services.AddCqrsGenerated(b => b
            .UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(5))
            .UseResilience(o => o.MaxRetries = 2)
            .UseRateLimiting(o => o.MaxTokens = 10));

        foreach (var (request, stream) in Behaviors)
        {
            Registrations(services, typeof(IPipelineBehavior<,>), request).Should().Be(1, $"{request.Name} is registered once");
            Registrations(services, typeof(IStreamPipelineBehavior<,>), stream).Should().Be(1, $"{stream.Name} is registered once");
        }

        services.Count(d => d.ServiceType == typeof(RequestRateLimiter)).Should().Be(1);

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<ResilienceOptions>>().Value.MaxRetries.Should().Be(2);
        provider.GetRequiredService<IOptions<TimeoutOptions>>().Value.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        provider.GetRequiredService<IOptions<RateLimitingOptions>>().Value.MaxTokens.Should().Be(10);

        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new TestCommand(), TestContext.Current.CancellationToken);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact(DisplayName = "Repeating an identical AddCqrsGenerated(builder) call registers no behavior or option validator twice")]
    public void Identical_builder_calls_do_not_stack()
    {
        static void Configure(ICqrsBuilder b) => b
            .UseLogging()
            .UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(5))
            .UseResilience(o => o.MaxRetries = 1)
            .UseRateLimiting(o => o.MaxTokens = 10);

        var services = new ServiceCollection();
        services.AddCqrsGenerated(Configure);
        services.AddCqrsGenerated(Configure);

        foreach (var (request, stream) in Behaviors)
        {
            Registrations(services, typeof(IPipelineBehavior<,>), request).Should().Be(1);
            Registrations(services, typeof(IStreamPipelineBehavior<,>), stream).Should().Be(1);
        }

        services.Count(d => d.ServiceType == typeof(IValidateOptions<ResilienceOptions>)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IValidateOptions<TimeoutOptions>)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IValidateOptions<RateLimitingOptions>)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IValidateOptions<BackgroundTaskQueueOptions>)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IValidateOptions<DispatcherOptions>)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IValidateOptions<OutboxOptions>)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IValidateOptions<NotificationOptions>)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IValidateOptions<CqrsStartupValidationOptions>)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IValidateOptions<OutboxProcessorOptions>)).Should().Be(1);
    }

    [Fact(DisplayName = "ConfigureProcessor calls compose, within one UseOutbox and across several")]
    public void Processor_configuration_composes()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseOutbox(o => o.ConfigureProcessor(p => p.BatchSize = 10).ConfigureProcessor(p => p.MaxAttempts = 7))
            .UseOutbox(o => o.ConfigureProcessor(p => p.PollingInterval = TimeSpan.FromSeconds(1))));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<OutboxProcessorOptions>>().Value;

        options.BatchSize.Should().Be(10);
        options.MaxAttempts.Should().Be(7);
        options.PollingInterval.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact(DisplayName = "An invalid core option is reported once, however many AddCqrsGenerated calls registered CQRSharp")]
    public void Core_option_failures_are_reported_once()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.ConfigureQueue(o => o.Capacity = 0));
        services.AddCqrsGenerated(b => b.UseLogging());
        services.AddCqrsGenerated();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<BackgroundTaskQueueOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().Which.Failures
            .Should().ContainSingle().Which.Should().Be("BackgroundTaskQueueOptions.Capacity must be greater than zero.");
    }

    [Theory(DisplayName = "ValidateOnStart survives another AddCqrsGenerated(builder) call that does not set a policy, in either order")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateOnStart_survives_another_builder_call(bool validateFirst)
    {
        var services = new ServiceCollection();
        if (validateFirst)
        {
            services.AddCqrsGenerated(b => b.ValidateOnStart());
            services.AddCqrsGenerated(b => b.UseLogging());
        }
        else
        {
            services.AddCqrsGenerated(b => b.UseLogging());
            services.AddCqrsGenerated(b => b.ValidateOnStart());
        }

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<CqrsStartupValidationOptions>>().Value.Policy
            .Should().Be(CqrsValidationPolicy.ThrowOnError);
    }

    [Fact(DisplayName = "A startup-validation policy configured before AddCqrsGenerated(builder) survives it")]
    public async Task Policy_configured_before_the_builder_survives()
    {
        var services = new ServiceCollection();
        services.Configure<CqrsStartupValidationOptions>(o => o.Policy = CqrsValidationPolicy.ThrowOnWarning);
        services.AddCqrsGenerated(b => b.UseLogging());

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<CqrsStartupValidationOptions>>().Value.Policy
            .Should().Be(CqrsValidationPolicy.ThrowOnWarning);
    }

    [Fact(DisplayName = "A builder that never calls ValidateOnStart leaves the policy unset, for the host environment to decide; ValidateOnStart(false) sets Off")]
    public async Task Policy_is_unset_unless_a_builder_sets_it()
    {
        var unset = new ServiceCollection();
        unset.AddCqrsGenerated(b => b.UseLogging());
        await using (var provider = unset.BuildServiceProvider())
            provider.GetRequiredService<IOptions<CqrsStartupValidationOptions>>().Value.Policy.Should().BeNull();

        var off = new ServiceCollection();
        off.AddCqrsGenerated(b => b.ValidateOnStart(false));
        await using (var provider = off.BuildServiceProvider())
            provider.GetRequiredService<IOptions<CqrsStartupValidationOptions>>().Value.Policy.Should().Be(CqrsValidationPolicy.Off);
    }

    [Fact(DisplayName = "Options configured by two AddCqrsGenerated(builder) calls compose")]
    public async Task Options_from_two_builder_calls_compose()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.ConfigureDispatcher(o => o.RunMode = RunMode.Queued));
        services.AddCqrsGenerated(b => b.ConfigureDispatcher(o => o.ScopeMode = ExecutionScopeMode.New));

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DispatcherOptions>>().Value;
        options.RunMode.Should().Be(RunMode.Queued);
        options.ScopeMode.Should().Be(ExecutionScopeMode.New);
    }

    [Fact(DisplayName = "An opt-out in one AddCqrsGenerated(builder) call does not remove what another call registered")]
    public void Opt_out_applies_to_its_own_call_only()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(_ => { });
        services.AddCqrsGenerated(b => b.UseValidation(false).UseExceptionHandling(false));

        Registrations(services, typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>)).Should().Be(1);
        Registrations(services, typeof(IPipelineBehavior<,>), typeof(ExceptionHandlingBehavior<,>)).Should().Be(1);
    }

    [Fact]
    public void UseTimeProvider_wins_regardless_of_position()
    {
        var fake = new FakeTimeProvider();

        // TimeProvider set first...
        var servicesFirst = new ServiceCollection();
        servicesFirst.AddCqrsGenerated(b => b
            .UseTimeProvider(fake)
            .UseLogging()
            .ValidateOnStart());

        // ...and set last. Either way it must be the authoritative TimeProvider.
        var servicesLast = new ServiceCollection();
        servicesLast.AddCqrsGenerated(b => b
            .UseLogging()
            .ValidateOnStart()
            .UseTimeProvider(fake));

        using var providerFirst = servicesFirst.BuildServiceProvider();
        using var providerLast = servicesLast.BuildServiceProvider();

        providerFirst.GetRequiredService<TimeProvider>().Should().BeSameAs(fake);
        providerLast.GetRequiredService<TimeProvider>().Should().BeSameAs(fake);
    }

    [Fact]
    public void UseTimeProvider_factory_that_resolves_TimeProvider_fails_fast_instead_of_hanging()
    {
        // A factory that resolves TimeProvider from the provider is self-referential: the factory IS the TimeProvider
        // registration, so sp.GetService<TimeProvider>() re-enters it, and unguarded that recursion hangs the container
        // (the ?? TimeProvider.System fallback is never reached). It must fail fast with a clear error instead.
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseTimeProvider(sp => sp.GetService<TimeProvider>() ?? TimeProvider.System));

        using var provider = services.BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<TimeProvider>();

        resolve.Should().Throw<InvalidOperationException>("the self-referential factory must fail fast, not deadlock")
            .WithMessage("*resolves TimeProvider from the service*");
    }

    [Fact]
    public void UseTimeProvider_factory_resolving_a_distinct_clock_type_still_works()
    {
        // The guard must not break the legitimate factory pattern: resolving a DIFFERENT clock type from the provider.
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton(clock); // registered under FakeTimeProvider, so RemoveAll<TimeProvider> leaves it intact
        services.AddCqrsGenerated(b => b
            .UseTimeProvider(sp => sp.GetRequiredService<FakeTimeProvider>()));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(clock);
    }

    [Fact]
    public async Task UseIdempotency_with_in_memory_store_registers_a_working_store()
    {
        // One cohesive verb selects the behavior and the store together.
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseInMemoryStore()));

        using var provider = services.BuildServiceProvider();
        var store = provider.GetService<IIdempotencyStore>();

        store.Should().NotBeNull("UseIdempotency(i => i.UseInMemoryStore()) registers the in-memory idempotency store");
        (await store!.TryClaimAsync("k", null, CancellationToken.None)).IsClaimed.Should().BeTrue();
        (await store.TryClaimAsync("k", null, CancellationToken.None)).IsClaimed.Should().BeFalse("the key is already claimed");
    }

    [Fact]
    public void UseOutbox_enables_the_mode_and_registers_a_store()
    {
        // One cohesive verb sets the mode and the store together.
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Transactional().UseInMemoryStore()));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<OutboxOptions>>().Value.Mode.Should().Be(OutboxMode.Transactional);
        provider.GetService<IOutboxStore>().Should().NotBeNull("UseOutbox registers the selected store");
    }

    [Fact]
    public void Without_UseOutbox_the_outbox_is_off_by_default()
    {
        // No outbox configuration: Mode is Disabled and no store is registered.
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<OutboxOptions>>().Value.Mode.Should().Be(OutboxMode.Disabled);
        provider.GetService<IOutboxStore>().Should().BeNull("the outbox is off unless explicitly enabled");
    }
}
