using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     An interceptor or exemption declared on a base request class applies to every request derived from it, as
///     <c>Type.GetCustomAttributes(inherit: true)</c> would read it: dispatched through this test assembly's generated
///     request metadata.
/// </summary>
public sealed class InheritedRequestAttributesTests
{
    [Fact(DisplayName = "An interceptor on a base request runs for the derived request")]
    public async Task Base_interceptor_runs()
    {
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new AuditedCreate(), TestContext.Current.CancellationToken);

        scope.ServiceProvider.GetRequiredService<InheritanceProbe>().Audited.Should().Equal(nameof(AuditedCreate));
    }

    [Fact(DisplayName = "An exemption on a base request skips the behavior for the derived request, and only for it")]
    public async Task Base_exemption_applies()
    {
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<InheritanceProbe>();

        await dispatcher.Send(new AuditedCreate(), TestContext.Current.CancellationToken);
        await dispatcher.Send(new PlainCreate(), TestContext.Current.CancellationToken);

        probe.Wrapped.Should().Equal([nameof(PlainCreate)], "the base request exempts AuditedCreate from the behavior");
    }

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddScoped<InheritanceProbe>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(InheritanceProbeBehavior<,>));
        return services.BuildServiceProvider();
    }

    public sealed class InheritanceProbe
    {
        public List<string> Audited { get; } = [];
        public List<string> Wrapped { get; } = [];
    }

    public sealed class InheritedAuditAttribute : Attribute, IPreHandlerAttribute
    {
        public int PreHandlerExecutionPriority => 0;

        public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            serviceProvider.GetRequiredService<InheritanceProbe>().Audited.Add(request.GetType().Name);
            return Task.CompletedTask;
        }
    }

    public sealed class InheritanceProbeBehavior<TRequest, TResult>(InheritanceProbe probe) : IPipelineBehavior<TRequest, TResult>
        where TRequest : IRequest
    {
        public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
        {
            if (request is AuditedCreate or PlainCreate) probe.Wrapped.Add(typeof(TRequest).Name);
            return next(cancellationToken);
        }
    }

    [InheritedAudit]
    [PipelineExemption(typeof(InheritanceProbeBehavior<,>))]
    public abstract class AuditedCommand : CommandBase;

    public sealed class AuditedCreate : AuditedCommand;

    public sealed class AuditedCreateHandler : ICommandHandler<AuditedCreate>
    {
        public Task<CommandResult> Handle(AuditedCreate command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
    }

    public sealed class PlainCreate : CommandBase;

    public sealed class PlainCreateHandler : ICommandHandler<PlainCreate>
    {
        public Task<CommandResult> Handle(PlainCreate command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
    }
}
