using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Modules;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The validators and exception hooks the generator discovers, next to the application's own registrations of
///     them: each implementation runs once whatever order the two were registered in, and the application's
///     registration is the one used.
/// </summary>
public sealed class DiscoveredServicesTests
{
    [Fact(DisplayName = "A discovered validator also registered by hand after AddCqrsGenerated reports its failures once")]
    public async Task Validator_registered_after_runs_once()
    {
        var failures = await ValidationFailures(services =>
        {
            services.AddCqrsGenerated(b => b.UseValidation());
            services.AddTransient<IRequestValidator<DiscoveredValidatedCommand>, DiscoveredCommandValidator>();
        });

        failures.Should().ContainSingle();
    }

    [Fact(DisplayName = "A discovered validator also registered by hand before AddCqrsGenerated reports its failures once")]
    public async Task Validator_registered_before_runs_once()
    {
        var failures = await ValidationFailures(services =>
        {
            services.AddTransient<IRequestValidator<DiscoveredValidatedCommand>, DiscoveredCommandValidator>();
            services.AddCqrsGenerated(b => b.UseValidation());
        });

        failures.Should().ContainSingle();
    }

    [Fact(DisplayName = "A discovered validator registered by hand with factories runs as registered: the application's registrations win")]
    public async Task Hand_registrations_replace_the_discovered_one()
    {
        var failures = await ValidationFailures(services =>
        {
            services.AddCqrsGenerated(b => b.UseValidation());
            services.AddTransient<IRequestValidator<DiscoveredValidatedCommand>>(_ => new DiscoveredCommandValidator("first"));
            services.AddTransient<IRequestValidator<DiscoveredValidatedCommand>>(_ => new DiscoveredCommandValidator("second"));
        });

        failures.Select(f => f.Message).Should().Equal("first", "second");
    }

    [Fact(DisplayName = "Without a hand registration the discovered validator runs")]
    public async Task Discovered_validator_runs()
    {
        var failures = await ValidationFailures(services => services.AddCqrsGenerated(b => b.UseValidation()));

        failures.Select(f => f.Message).Should().Equal(DiscoveredCommandValidator.DefaultMessage);
    }

    [Theory(DisplayName = "On the closed-behavior path (Native AOT) the validation behavior runs the discovered validator, once")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_path_validation_merges_discovered_validators(bool alsoRegisteredByHand)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        services.AddCqrsGenerated(b => b.UseValidation());
        if (alsoRegisteredByHand) services.AddTransient<IRequestValidator<DiscoveredValidatedCount>, DiscoveredCountValidator>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var send = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new DiscoveredValidatedCount(), TestContext.Current.CancellationToken);

        (await send.Should().ThrowAsync<RequestValidationException>()).Which.Failures.Should().ContainSingle();
    }

    [Fact(DisplayName = "A discovered exception action also registered by hand after AddCqrsGenerated runs once")]
    public async Task Exception_action_registered_after_runs_once()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false).UseExceptionHandling());
        services.AddTransient<IRequestExceptionAction<DiscoveredFailingCommand, InvalidOperationException>, DiscoveredFailureAction>();
        services.AddSingleton<DiscoveredFailureProbe>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var send = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new DiscoveredFailingCommand(), TestContext.Current.CancellationToken);

        await send.Should().ThrowAsync<InvalidOperationException>();
        scope.ServiceProvider.GetRequiredService<DiscoveredFailureProbe>().Actions.Should().Be(1);
    }

    [Theory(DisplayName = "A discovered closed pipeline behavior also registered by hand runs once, before or after AddCqrsGenerated, on either resolution path")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Closed_behavior_registered_by_hand_runs_once(bool registeredBefore, bool closedResolution)
    {
        var services = new ServiceCollection();
        if (closedResolution) services.AddSingleton(new ClosedBehaviorResolution { Enabled = true });
        if (registeredBefore) services.AddTransient<IPipelineBehavior<DiscoveredBehaviorQuery, int>, DiscoveredQueryBehavior>();
        services.AddCqrsGenerated();
        if (!registeredBefore) services.AddTransient<IPipelineBehavior<DiscoveredBehaviorQuery, int>, DiscoveredQueryBehavior>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var query = new DiscoveredBehaviorQuery();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(query, TestContext.Current.CancellationToken);

        query.BehaviorRuns.Should().Be(1);
    }

    [Fact(DisplayName = "Without a hand registration the discovered closed pipeline behaviors run, request and stream alike")]
    public async Task Discovered_closed_behaviors_run()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var query = new DiscoveredBehaviorQuery();
        var stream = new DiscoveredBehaviorStream();

        await dispatcher.Send(query, TestContext.Current.CancellationToken);
        await foreach (var _ in dispatcher.Stream(stream, TestContext.Current.CancellationToken)) { }

        query.BehaviorRuns.Should().Be(1);
        stream.BehaviorRuns.Should().Be(1);
    }

    [Fact(DisplayName = "A discovered closed stream behavior also registered by hand runs once")]
    public async Task Closed_stream_behavior_registered_by_hand_runs_once()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddTransient<IStreamPipelineBehavior<DiscoveredBehaviorStream, int>, DiscoveredStreamBehavior>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var stream = new DiscoveredBehaviorStream();

        await foreach (var _ in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(stream, TestContext.Current.CancellationToken)) { }

        stream.BehaviorRuns.Should().Be(1);
    }

    [Theory(DisplayName = "A discovered closed notification behavior runs without a hand registration, and once with one")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovered_closed_notification_behavior_runs_once(bool registeredByHand)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        if (registeredByHand)
            services.AddTransient<INotificationPipelineBehavior<DiscoveredBehaviorNotification>, DiscoveredNotificationBehavior>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var notification = new DiscoveredBehaviorNotification();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(notification, TestContext.Current.CancellationToken);

        notification.BehaviorRuns.Should().Be(1);
    }

    [Fact(DisplayName = "The diagnostics describe a discovered closed behavior registered by hand once")]
    public async Task Closed_behavior_is_described_once()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddTransient<IPipelineBehavior<DiscoveredBehaviorQuery, int>, DiscoveredQueryBehavior>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeAllRequests()
            .Single(b => b.RequestType == typeof(DiscoveredBehaviorQuery))
            .Pipeline.Should().ContainSingle(b => b.BehaviorType == typeof(DiscoveredQueryBehavior));
    }

    [Fact(DisplayName = "Merge: discovered services first, less those the application registered, then the application's in order")]
    public void Merge_order()
    {
        var discoveredA = new FirstService();
        var discoveredB = new SecondService();
        var registeredB = new SecondService();
        var registeredB2 = new SecondService();
        var registeredC = new ThirdService();

        DiscoveredServices.Merge<IMergedService>([discoveredA, discoveredB], [registeredB, registeredC, registeredB2])
            .Should().Equal(discoveredA, registeredB, registeredC, registeredB2);
        DiscoveredServices.Merge<IMergedService>([discoveredA], []).Should().Equal(discoveredA);
        DiscoveredServices.Merge<IMergedService>([], [registeredC]).Should().Equal(registeredC);
    }

    private static async Task<IReadOnlyList<ValidationFailure>> ValidationFailures(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var send = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new DiscoveredValidatedCommand(), TestContext.Current.CancellationToken);

        return (await send.Should().ThrowAsync<RequestValidationException>()).Which.Failures;
    }

    public interface IMergedService;

    private sealed class FirstService : IMergedService;

    private sealed class SecondService : IMergedService;

    private sealed class ThirdService : IMergedService;
}

public sealed class DiscoveredValidatedCommand : CommandBase;

public sealed class DiscoveredValidatedCommandHandler : ICommandHandler<DiscoveredValidatedCommand>
{
    public Task<CommandResult> Handle(DiscoveredValidatedCommand command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}

public sealed class DiscoveredCommandValidator(string message) : IRequestValidator<DiscoveredValidatedCommand>
{
    public const string DefaultMessage = "discovered";

    public DiscoveredCommandValidator() : this(DefaultMessage)
    {
    }

    public Task<ValidationFailure[]> ValidateAsync(DiscoveredValidatedCommand request, CancellationToken cancellationToken)
        => Task.FromResult(new[] { new ValidationFailure("DISCOVERED", message) });
}

public sealed class DiscoveredValidatedCount : QueryBase<int>;

public sealed class DiscoveredValidatedCountHandler : IQueryHandler<DiscoveredValidatedCount, int>
{
    public Task<int> Handle(DiscoveredValidatedCount query, CancellationToken cancellationToken) => Task.FromResult(1);
}

public sealed class DiscoveredCountValidator : IRequestValidator<DiscoveredValidatedCount>
{
    public Task<ValidationFailure[]> ValidateAsync(DiscoveredValidatedCount request, CancellationToken cancellationToken)
        => Task.FromResult(new[] { new ValidationFailure("COUNT", "discovered") });
}

public sealed class DiscoveredFailingCommand : CommandBase;

public sealed class DiscoveredFailingCommandHandler : ICommandHandler<DiscoveredFailingCommand>
{
    public Task<CommandResult> Handle(DiscoveredFailingCommand command, CancellationToken cancellationToken) => throw new InvalidOperationException("failed");
}

public sealed class DiscoveredFailureProbe
{
    private int _actions;

    public int Actions => Volatile.Read(ref _actions);

    public void Record() => Interlocked.Increment(ref _actions);
}

public sealed class DiscoveredFailureAction(DiscoveredFailureProbe probe) : IRequestExceptionAction<DiscoveredFailingCommand, InvalidOperationException>
{
    public Task Execute(DiscoveredFailingCommand request, InvalidOperationException exception, CancellationToken cancellationToken)
    {
        probe.Record();
        return Task.CompletedTask;
    }
}

// A behavior the generator discovers (a closed, public pipeline behavior); it counts its runs on the request itself, so
// describing every request's pipeline (the startup validator does) needs nothing registered for it.
public sealed class DiscoveredBehaviorQuery : QueryBase<int>
{
    public int BehaviorRuns { get; set; }
}

public sealed class DiscoveredBehaviorQueryHandler : IQueryHandler<DiscoveredBehaviorQuery, int>
{
    public Task<int> Handle(DiscoveredBehaviorQuery query, CancellationToken cancellationToken) => Task.FromResult(1);
}

public sealed class DiscoveredQueryBehavior : IPipelineBehavior<DiscoveredBehaviorQuery, int>
{
    public Task<int> Handle(DiscoveredBehaviorQuery request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
    {
        request.BehaviorRuns++;
        return next(cancellationToken);
    }
}

public sealed class DiscoveredBehaviorStream : StreamRequestBase<int>
{
    public int BehaviorRuns { get; set; }
}

public sealed class DiscoveredBehaviorStreamHandler : IStreamRequestHandler<DiscoveredBehaviorStream, int>
{
    public IAsyncEnumerable<int> Handle(DiscoveredBehaviorStream request, CancellationToken cancellationToken)
        => CQRSharp.Tests.Pipelines.StreamBehaviorFixtures.Produce([1], cancellationToken);
}

public sealed class DiscoveredStreamBehavior : IStreamPipelineBehavior<DiscoveredBehaviorStream, int>
{
    public IAsyncEnumerable<int> Handle(DiscoveredBehaviorStream request, StreamHandlerDelegate<int> next, CancellationToken cancellationToken)
    {
        request.BehaviorRuns++;
        return next(cancellationToken);
    }
}

/// <summary>Only the discovered-services tests publish it: its closed behavior is discovered for the whole assembly.</summary>
public sealed class DiscoveredBehaviorNotification : INotification
{
    public int BehaviorRuns { get; set; }
}

public sealed class DiscoveredNotificationBehavior : INotificationPipelineBehavior<DiscoveredBehaviorNotification>
{
    public Task Handle(DiscoveredBehaviorNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
    {
        notification.BehaviorRuns++;
        return next(cancellationToken);
    }
}

