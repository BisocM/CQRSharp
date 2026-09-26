using CQRSharp.Tests.ExternalModule;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     How the generated modules register handlers: next to the consumer's own registrations, next to a referenced
///     assembly's module, and for a closed generic request.
/// </summary>
public sealed class HandlerRegistrationTests
{
    [Fact(DisplayName = "A handler lifetime the consumer registered before AddCqrsGenerated stands, and is what delivery uses")]
    public async Task Consumer_handler_lifetime_wins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<BaseAuditRecorder>();
        services.AddSingleton<BaseAuditHandler>();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        first.ServiceProvider.GetRequiredService<BaseAuditHandler>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<BaseAuditHandler>());
    }

    [Fact(DisplayName = "A notification handler is registered by its type only: the container's INotificationHandler<T> registrations are the application's own")]
    public async Task Notification_handler_gets_no_interface_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<BaseAuditRecorder>();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetService<BaseAuditHandler>().Should().NotBeNull();
        scope.ServiceProvider.GetServices<INotificationHandler<BaseAuditedNotification>>().Should().BeEmpty();
    }

    [Fact(DisplayName = "An interface registration the consumer made by hand is not duplicated by the generated forwarder")]
    public async Task Manual_interface_registration_is_not_duplicated()
    {
        var services = new ServiceCollection();
        services.AddTransient<IQueryHandler<EchoQuery<int>, int>, EchoIntQueryHandler>();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetServices<IQueryHandler<EchoQuery<int>, int>>().Should().ContainSingle();
    }

    [Fact(DisplayName = "A request handled by both a referenced assembly and the host is served by the host")]
    public async Task Host_handler_wins_over_a_referenced_assembly()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new SharedCommand(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue("the host's handler registers last and wins");
    }

    [Fact(DisplayName = "A closed generic request whose handler this assembly declares is routed")]
    public async Task Closed_generic_request_is_routed()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new EchoQuery<int> { Value = 7 }, TestContext.Current.CancellationToken)).Should().Be(7);
    }
}

public sealed class SharedCommandHandler : ICommandHandler<SharedCommand>
{
    public Task<CommandResult> Handle(SharedCommand command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}

public sealed class EchoQuery<T> : QueryBase<T>
{
    public T Value { get; init; } = default!;
}

public sealed class EchoIntQueryHandler : IQueryHandler<EchoQuery<int>, int>
{
    public Task<int> Handle(EchoQuery<int> query, CancellationToken cancellationToken) => Task.FromResult(query.Value);
}
