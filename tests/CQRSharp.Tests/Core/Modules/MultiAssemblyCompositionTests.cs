using CQRSharp.Persistence;
using CQRSharp.Tests.ExternalModule;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     How the generated modules compose across assemblies. This test assembly is the composition root; its single
///     <c>AddCqrsGenerated()</c> wires its own module and the referenced <c>CQRSharp.Tests.ExternalModule</c> assembly's,
///     including that assembly's <see langword="internal" /> handlers, which the root cannot name. The merged registries
///     and the composite dispatchers and serializer route each request, notification and durable notification name to
///     the module that owns it.
/// </summary>
public sealed class MultiAssemblyCompositionTests
{
    [Fact(DisplayName = "Multi-assembly: a query whose (internal) handler lives in a referenced assembly dispatches")]
    public async Task CrossAssembly_Query_Dispatches()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await Dispatcher(scope).Send(new ExternalQuery { Value = "hi" }, TestContext.Current.CancellationToken);

        result.Should().Be("external:hi", "the referenced assembly's module registers its own internal handler");
    }

    [Fact(DisplayName = "Multi-assembly: a notification handled (internal) in a referenced assembly is published")]
    public async Task CrossAssembly_Notification_Publishes()
    {
        ExternalSignals.Reset();
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        await Dispatcher(scope).Publish(new ExternalNotification { Message = "ping" }, TestContext.Current.CancellationToken);

        ExternalSignals.NotificationHandled.Should().Be(1, "the composite notification dispatcher routes to the owning module");
    }

    [Fact(DisplayName = "Multi-assembly: the composite serializer names and round-trips the durable notifications of both modules")]
    public async Task CrossAssembly_Serializer_Names_Both_Modules()
    {
        await using var provider = BuildProvider();
        var serializer = provider.GetRequiredService<INotificationSerializer>();

        serializer.TryGetNotificationName(typeof(TestNotification), out var localName).Should().BeTrue("this assembly's module names it");
        localName.Should().Be("test.notification");
        serializer.TryGetNotificationName(typeof(ExternalDurableNotification), out var externalName).Should().BeTrue("the referenced module names it");
        externalName.Should().Be("tests.external.durable");
        serializer.TryGetNotificationName(typeof(ExternalNotification), out _).Should().BeFalse("neither module names a notification without [NotificationName]");

        serializer.Deserialize(localName!, serializer.Serialize(new TestNotification())).Should().BeOfType<TestNotification>();
        serializer.Deserialize(externalName!, serializer.Serialize(new ExternalDurableNotification { Message = "hi" }))
            .Should().BeOfType<ExternalDurableNotification>().Which.Message.Should().Be("hi");
        serializer.Deserialize("unknown.notification", serializer.Serialize(new TestNotification()))
            .Should().BeNull("no module owns that name");
    }

    [Fact(DisplayName = "Multi-assembly: the local assembly's own handlers still dispatch alongside the referenced module")]
    public async Task LocalAndExternal_CoexistInOneProvider()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var cqrs = Dispatcher(scope);

        // The external query (referenced assembly) and a local command (this assembly) both resolve from the single
        // merged set of registries: neither module clobbers the other.
        var external = await cqrs.Send(new ExternalQuery { Value = "x" }, TestContext.Current.CancellationToken);
        external.Should().Be("external:x");

        var local = await cqrs.Send(new LocalCoexistCommand(), TestContext.Current.CancellationToken);
        local.IsSuccess.Should().BeTrue();
    }

    [Fact(DisplayName = "Multi-assembly: an exception action in a referenced assembly and the root's exception handler for the same pair both run")]
    public async Task Exception_hook_roles_split_across_assemblies_both_run()
    {
        ExternalSignals.Reset();
        await using var provider = BuildProvider(b => b.UseExceptionHandling());
        await using var scope = provider.CreateAsyncScope();

        var result = await Dispatcher(scope).Send(new ExternalFailingQuery(), TestContext.Current.CancellationToken);

        result.Should().Be(RootExternalFailureRecovery.Recovered, "the root's handler handles the exception");
        ExternalSignals.ExceptionActionRuns.Should().Be(1, "the referenced assembly's action runs, once");
    }

    [Fact(DisplayName = "Multi-assembly: the root's exception action and a referenced assembly's exception handler for the same pair both run")]
    public async Task Exception_hook_roles_split_the_other_way_both_run()
    {
        var before = RootExternalRecoveryAction.Runs;
        await using var provider = BuildProvider(b => b.UseExceptionHandling());
        await using var scope = provider.CreateAsyncScope();

        var result = await Dispatcher(scope).Send(new ExternalRecoveredQuery(), TestContext.Current.CancellationToken);

        result.Should().Be(7, "the referenced assembly's handler handles the exception");
        (RootExternalRecoveryAction.Runs - before).Should().Be(1, "the root's action runs, once");
    }

    [Fact(DisplayName = "Multi-assembly: a stream request whose handler lives in a referenced assembly streams")]
    public async Task CrossAssembly_Stream_Streams()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var items = new List<int>();
        await foreach (var item in Dispatcher(scope).Stream(new ExternalCountdown { From = 3 }, TestContext.Current.CancellationToken))
            items.Add(item);

        items.Should().Equal(3, 2, 1);
    }

    [Fact(DisplayName = "Multi-assembly: a custom context declared, created and used in a referenced assembly reaches its handler")]
    public async Task CrossAssembly_Context_Factory_Is_Used()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var origin = await Dispatcher(scope).Send(new ExternalContextQuery(), TestContext.Current.CancellationToken);

        origin.Should().Be("external-factory");
    }

    [Fact(DisplayName = "Multi-assembly: a referenced assembly's durable notification is subscribed to by its handler")]
    public async Task CrossAssembly_Durable_Notification_Is_Subscribed()
    {
        await using var provider = BuildProvider();

        provider.GetRequiredService<CQRSharp.Core.Notifications.INotificationSubscriptionRegistry>()
            .GetSubscriptions(typeof(ExternalDurableNotification))
            .Should().ContainSingle().Which.HandlerName.Should().Be("CQRSharp.Tests.ExternalModule.ExternalDurableNotificationHandler");
    }

    private static ServiceProvider BuildProvider(Action<CQRSharp.Pipelines.ICqrsBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        if (configure is null) services.AddCqrsGenerated();
        else services.AddCqrsGenerated(configure);
        return services.BuildServiceProvider();
    }

    // The dispatcher and its composites are scoped.
    private static ICqrsDispatcher Dispatcher(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
}

/// <summary>A local command in the composition-root assembly, to assert local and referenced modules coexist.</summary>
public sealed class LocalCoexistCommand : CommandBase;

internal sealed class LocalCoexistCommandHandler : ICommandHandler<LocalCoexistCommand>
{
    public Task<CommandResult> Handle(LocalCoexistCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

/// <summary>The root's half of the split exception hooks: the handler for a pair whose action a referenced assembly declares.</summary>
internal sealed class RootExternalFailureRecovery : IRequestExceptionHandler<ExternalFailingQuery, int, InvalidOperationException>
{
    public const int Recovered = 42;

    public Task Handle(ExternalFailingQuery request, InvalidOperationException exception, RequestExceptionHandlerState<int> state, CancellationToken cancellationToken)
    {
        state.SetHandled(Recovered);
        return Task.CompletedTask;
    }
}

/// <summary>The root's half of the reverse split: the action for a pair whose handler a referenced assembly declares.</summary>
internal sealed class RootExternalRecoveryAction : IRequestExceptionAction<ExternalRecoveredQuery, InvalidOperationException>
{
    private static int _runs;

    public static int Runs => Volatile.Read(ref _runs);

    public Task Execute(ExternalRecoveredQuery request, InvalidOperationException exception, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runs);
        return Task.CompletedTask;
    }
}
