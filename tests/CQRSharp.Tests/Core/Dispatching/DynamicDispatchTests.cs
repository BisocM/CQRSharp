using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Dispatch by a request's runtime type: <c>Send(object)</c> and <c>Stream(object)</c> find the request's route and
///     box what it returns or yields, reject what is not a request of their kind, and a request sent under a widened
///     result type (<c>IRequest&lt;object&gt;</c>) is dispatched rather than failing a cast.
/// </summary>
public sealed class DynamicDispatchTests
{
    [Fact]
    public async Task Send_object_executes_command_and_returns_boxed_response()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        object request = new TestCommand();
        var result = await cqrs.Send(request, TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult>();
        ((CommandResult)result!).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Send_object_executes_query_and_returns_boxed_response()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        object request = new TestQuery();
        var result = await cqrs.Send(request, TestContext.Current.CancellationToken);

        result.Should().BeOfType<TestQueryResult>();
        ((TestQueryResult)result!).Value.Should().Be("Success");
    }

    [Fact]
    public async Task Send_object_rejects_non_request_objects()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var act = async () => await cqrs.Send(new object());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Stream_object_executes_by_runtime_type_and_boxes_items()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        object request = new TestStreamRequest(2);
        var results = new List<object?>();
        await foreach (var item in cqrs.Stream(request, TestContext.Current.CancellationToken))
            results.Add(item);

        results.Should().Equal(0, 1);
    }

    [Fact]
    public async Task Send_object_throws_for_stream_requests()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var act = async () => await cqrs.Send((object)new TestStreamRequest(1));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Stream requests must be executed via Stream(*)*");
    }

    [Fact(DisplayName = "A request sent under a widened result type (IRequest<object>) is dispatched rather than failing a cast")]
    public async Task Widened_request_type_is_dispatched()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        IRequest<object> widened = new NamingQuery();
        (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(widened, TestContext.Current.CancellationToken)).Should().Be("name");
    }

    [Fact(DisplayName = "A plain command sent under a widened result type (IRequest<object>) is dispatched")]
    public async Task Widened_plain_command_is_dispatched()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        IRequest<object> widened = new QueuedInnerCommand();
        (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(widened, TestContext.Current.CancellationToken)).Should().BeOfType<CommandResult>();
    }
}
