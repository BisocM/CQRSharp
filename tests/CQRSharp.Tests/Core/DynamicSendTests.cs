using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

public sealed class DynamicSendTests
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
        var result = await cqrs.Send(request);

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
        var result = await cqrs.Send(request);

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
}

