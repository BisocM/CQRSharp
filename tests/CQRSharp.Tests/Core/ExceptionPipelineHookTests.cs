using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Pipelines.Extensions;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

public sealed class ExceptionPipelineHookTests
{
    [Fact]
    public async Task Exception_actions_run_even_when_exception_not_handled()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddCqrsPipelinePack(pack => { pack.IncludeValidation = false; });
        services.AddSingleton<ExceptionHookProbe>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>();

        var act = async () => await cqrs.Send(new ActionOnlyExceptionCommand());

        await act.Should().ThrowAsync<ActionOnlyException>();
        probe.ActionCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Exception_handlers_can_convert_exception_into_response()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddCqrsPipelinePack(pack => { pack.IncludeValidation = false; });
        services.AddSingleton<ExceptionHookProbe>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>();

        var result = await cqrs.Send(new HandledExceptionCommand());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("handled");
        probe.HandledHandlerCalls.Should().Be(1);
    }

    [Fact]
    public async Task Most_specific_exception_handler_executes_first()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddCqrsPipelinePack(pack => { pack.IncludeValidation = false; });
        services.AddSingleton<ExceptionHookProbe>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>();

        var result = await cqrs.Send(new DerivedExceptionCommand());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("derived");
        probe.DerivedHandlerCalls.Should().Be(1);
        probe.BaseHandlerCalls.Should().Be(0);
    }
}

