using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Tests.ExternalModule;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.PlugAndPlay;

/// <summary>
///     Proves CQRSharp spans multiple assemblies. This test assembly is the composition root; its single
///     <c>AddCqrsGenerated()</c> wires its own module AND the referenced <c>CQRSharp.Tests.ExternalModule</c> assembly's
///     module — including that assembly's <see langword="internal" /> handlers, which the root cannot name directly.
///     This is the regression test for the multi-assembly failure (CS0121 from colliding generated entry points, plus
///     dispatchers that only knew one assembly's requests) the per-assembly module design fixes.
/// </summary>
public sealed class MultiAssemblyTests
{
    private static ICqrsDispatcher BuildDispatcher()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        var provider = services.BuildServiceProvider();
        // Resolve in a scope: the dispatcher and its composites are scoped.
        return provider.CreateScope().ServiceProvider.GetRequiredService<ICqrsDispatcher>();
    }

    [Fact(DisplayName = "Multi-assembly: a query whose (internal) handler lives in a referenced assembly dispatches")]
    public async Task CrossAssembly_Query_Dispatches()
    {
        var cqrs = BuildDispatcher();

        var result = await cqrs.Send(new ExternalQuery { Value = "hi" });

        result.Should().Be("external:hi", "the referenced assembly's module registers its own internal handler");
    }

    [Fact(DisplayName = "Multi-assembly: a notification handled (internal) in a referenced assembly is published")]
    public async Task CrossAssembly_Notification_Publishes()
    {
        ExternalSignals.Reset();
        var cqrs = BuildDispatcher();

        await cqrs.Publish(new ExternalNotification { Message = "ping" });

        ExternalSignals.NotificationHandled.Should().Be(1, "the composite notification dispatcher routes to the owning module");
    }

    [Fact(DisplayName = "Multi-assembly: the local assembly's own handlers still dispatch alongside the referenced module")]
    public async Task LocalAndExternal_CoexistInOneProvider()
    {
        var cqrs = BuildDispatcher();

        // The external query (referenced assembly) and a local ping command (this assembly) both resolve from the
        // single merged set of registries — neither module clobbers the other.
        var external = await cqrs.Send(new ExternalQuery { Value = "x" });
        external.Should().Be("external:x");

        var local = await cqrs.Send(new LocalCoexistCommand());
        local.IsSuccess.Should().BeTrue();
    }
}

/// <summary>A local command in the composition-root assembly, to assert local + referenced modules coexist.</summary>
public sealed class LocalCoexistCommand : CQRSharp.Abstractions.Interfaces.Markers.Command.CommandBase;

internal sealed class LocalCoexistCommandHandler
    : CQRSharp.Abstractions.Interfaces.Handlers.ICommandHandler<LocalCoexistCommand>
{
    public Task<CQRSharp.Abstractions.Models.Commands.CommandResult> Handle(
        LocalCoexistCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CQRSharp.Abstractions.Models.Commands.CommandResult.FromSuccess());
}
