using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

public sealed class StreamingTests
{
    [Fact]
    public async Task Stream_executes_stream_request_and_returns_items()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var results = new List<int>();
        await foreach (var item in cqrs.Stream(new TestStreamRequest(3)))
            results.Add(item);

        results.Should().Equal(0, 1, 2);
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
        await foreach (var item in cqrs.Stream(request))
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
}

