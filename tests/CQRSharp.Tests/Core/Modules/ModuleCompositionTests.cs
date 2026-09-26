using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Registries;
using CQRSharp.Persistence;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The composition's merge rules, with hand-built modules standing in for generated ones, so a clash between modules
///     can be tested without declaring it in a real module (where it would break every other test): the last module
///     wins a request (its metadata and its invoker), exception hooks are merged per pair and role, the default context always has a source, and a
///     notification name two modules claim is kept by the last one and reported.
/// </summary>
public sealed class ModuleCompositionTests
{
    [Fact(DisplayName = "Where two modules handle one request, the last registered wins it: its metadata, and the invoker that runs when it is sent")]
    public async Task Last_module_wins_a_request()
    {
        var invoked = new List<string>();
        var library = new TestModule
        {
            RequestMetadata = new Dictionary<Type, RequestMetadata> { [typeof(ComposedCommand)] = Metadata(typeof(LibraryHandler)) },
            HandlerInvokers = new Dictionary<Type, Delegate> { [typeof(ComposedCommand)] = Invoker(invoked, "library") },
            RequestRoutes = new Dictionary<Type, RequestRoute> { [typeof(ComposedCommand)] = RequestRoute.Command<ComposedCommand>() }
        };
        var host = new TestModule
        {
            RequestMetadata = new Dictionary<Type, RequestMetadata> { [typeof(ComposedCommand)] = Metadata(typeof(HostHandler)) },
            HandlerInvokers = new Dictionary<Type, Delegate> { [typeof(ComposedCommand)] = Invoker(invoked, "host") },
            RequestRoutes = new Dictionary<Type, RequestRoute> { [typeof(ComposedCommand)] = RequestRoute.Command<ComposedCommand>() }
        };
        var services = TestModule.Compose(new ServiceCollection(), library, host);
        services.AddTransient<LibraryHandler>();
        services.AddTransient<HostHandler>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        provider.GetRequiredService<IRequestRegistry>().TryGetRequestMetadata(typeof(ComposedCommand), out var metadata).Should().BeTrue();
        metadata!.HandlerType.Should().Be(typeof(HostHandler));
        (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new ComposedCommand(), TestContext.Current.CancellationToken))
            .IsSuccess.Should().BeTrue();
        invoked.Should().Equal("host:" + nameof(HostHandler));
    }

    // A generated invoker's shape: it records which module's invoker ran, over which handler instance.
    private static Func<object, ComposedCommand, CancellationToken, Task<CommandResult>> Invoker(List<string> invoked, string module)
        => (handler, _, _) =>
        {
            invoked.Add($"{module}:{handler.GetType().Name}");
            return Task.FromResult(CommandResult.FromSuccess());
        };

    [Fact(DisplayName = "An exception action declared in one module and a handler in another for one pair both run, each once")]
    public async Task Exception_hook_roles_from_two_modules_are_merged()
    {
        var runs = new List<string>();
        var library = new TestModule { ExceptionHooks = [Hook(runs, actions: "library-action")] };
        var host = new TestModule { ExceptionHooks = [Hook(runs, handlers: "host-handler")] };
        await using var provider = TestModule.Compose(new ServiceCollection(), library, host).BuildServiceProvider();

        var outcome = await Invoke(provider);

        runs.Should().Equal("library-action", "host-handler");
        outcome.Handled.Should().BeTrue();
        outcome.Response.Should().Be("host-handler");
    }

    [Fact(DisplayName = "An exception hook pair both modules declare in full runs each role once, not once per module")]
    public async Task Exception_hook_pair_declared_twice_runs_once()
    {
        var runs = new List<string>();
        var library = new TestModule { ExceptionHooks = [Hook(runs, actions: "actions", handlers: "handlers")] };
        var host = new TestModule { ExceptionHooks = [Hook(runs, actions: "actions", handlers: "handlers")] };
        await using var provider = TestModule.Compose(new ServiceCollection(), library, host).BuildServiceProvider();

        await Invoke(provider);

        runs.Should().Equal("actions", "handlers");
    }

    [Fact(DisplayName = "The default context always has a source, served by the built-in factory, even with no module mapping it")]
    public async Task Default_context_always_has_a_source()
    {
        await using var provider = TestModule.Compose(new ServiceCollection()).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var source = provider.GetRequiredService<IContextFactoryRegistry>().TryGetSource(typeof(RequestContextBase));

        source.Should().NotBeNull();
        source!.ResolveFactory(scope.ServiceProvider).Should().BeOfType<DefaultRequestContextFactory>();
    }

    [Fact(DisplayName = "A notification name two modules give different types: the last module keeps it, the other type is not durable, CQRCONF010 reports it")]
    public async Task Stable_name_clash_is_resolved_and_reported()
    {
        var library = NamingModule<LibraryEvent>("orders.placed");
        var host = NamingModule<HostEvent>("orders.placed");
        var services = TestModule.Compose(new ServiceCollection(), library, host);
        services.AddInMemoryOutboxStore();
        services.Configure<OutboxOptions>(o => o.Mode = OutboxMode.Enabled);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var serializer = provider.GetRequiredService<INotificationSerializer>();

        serializer.TryGetNotificationName(typeof(HostEvent), out var name).Should().BeTrue();
        name.Should().Be("orders.placed");
        serializer.TryGetNotificationName(typeof(LibraryEvent), out _).Should().BeFalse("stored under the name, it would be read back as the host's type");
        serializer.Deserialize("orders.placed", serializer.Serialize(new HostEvent())).Should().BeOfType<HostEvent>();

        var issue = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeConfiguration()
            .Should().ContainSingle(i => i.Code == "CQRCONF010").Subject;
        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Error);
        issue.Message.Should().Contain("orders.placed").And.Contain(typeof(LibraryEvent).FullName!).And.Contain(typeof(HostEvent).FullName!);
    }

    private static RequestMetadata Metadata(Type handlerType)
        => new(handlerType, typeof(RequestContextBase), [], [], []);

    private static RequestExceptionHook Hook(List<string> runs, string? actions = null, string? handlers = null)
        => new(
            typeof(ComposedCommand),
            typeof(InvalidOperationException),
            2,
            actions is null ? null : (_, _, _, _) =>
            {
                runs.Add(actions);
                return Task.FromResult(RequestExceptionHandlingOutcome.NotHandled);
            },
            handlers is null ? null : (_, _, _, _) =>
            {
                runs.Add(handlers);
                return Task.FromResult(RequestExceptionHandlingOutcome.HandledWith(handlers));
            });

    private static Task<RequestExceptionHandlingOutcome> Invoke(IServiceProvider provider)
    {
        provider.GetRequiredService<IRequestExceptionHookRegistry>().TryGetInvoker(typeof(ComposedCommand), out var invoker).Should().BeTrue();
        return invoker!(provider, new ComposedCommand(), new InvalidOperationException(), CancellationToken.None);
    }

    private static TestModule NamingModule<TNotification>(string name) where TNotification : INotification, new()
        => new()
        {
            NotificationRoutes = new Dictionary<Type, NotificationRoute> { [typeof(TNotification)] = NotificationRoute.For<TNotification>() },
            OutboxSerializer = new SingleTypeNotificationSerializer<TNotification>(name)
        };

    private sealed class ComposedCommand : CommandBase;

    private sealed class LibraryHandler;

    private sealed class HostHandler;

    private sealed record LibraryEvent : INotification;

    private sealed record HostEvent : INotification;
}
