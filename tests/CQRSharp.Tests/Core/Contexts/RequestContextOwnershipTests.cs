using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The dispatcher owns a request's context: a request bound from an HTTP body or read from a message cannot carry one
///     (the property is not settable from outside), and a context set on the request before it is dispatched is replaced
///     by the context factory's, on every dispatch path, so the identity a handler reads always comes from the application.
/// </summary>
public sealed class RequestContextOwnershipTests
{
    [Fact(DisplayName = "A request deserialized from JSON carries no context, whatever the payload says")]
    public void Context_cannot_be_bound_from_a_body()
    {
        var request = JsonSerializer.Deserialize<TenantProbeQuery>("""{"context":{"tenant":"victim-tenant"}}""", JsonSerializerOptions.Web);

        request.Should().NotBeNull();
        request!.Context.Should().BeNull();
    }

    [Fact(DisplayName = "A custom context set before Send is replaced by the factory's")]
    public async Task Preset_custom_context_is_replaced()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var query = new TenantProbeQuery();
        ((IRequest)query).Context = new TenantProbeContext { Tenant = "victim-tenant" };

        var tenant = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(query, TestContext.Current.CancellationToken);

        tenant.Should().Be(TenantProbeContextFactory.Tenant);
    }

    [Fact(DisplayName = "A default context set before Send is replaced by one the dispatcher stamps")]
    public async Task Preset_default_context_is_replaced()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        await using var provider = Build(services => services.AddSingleton<TimeProvider>(clock));
        await using var scope = provider.CreateAsyncScope();
        var query = new DefaultContextProbeQuery();
        ((IRequest)query).Context = new RequestContextBase(new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var context = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(query, TestContext.Current.CancellationToken);

        context.CreatedAt.Should().Be(clock.GetUtcNow().UtcDateTime);
    }

    [Fact(DisplayName = "A custom context set before Stream is replaced by the factory's, on the direct stream path too")]
    public async Task Preset_stream_context_is_replaced()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var request = new TenantProbeStream();
        ((IRequest)request).Context = new TenantProbeContext { Tenant = "victim-tenant" };

        var tenants = new List<string>();
        await foreach (var tenant in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(request, TestContext.Current.CancellationToken))
            tenants.Add(tenant);

        tenants.Should().Equal(TenantProbeContextFactory.Tenant);
    }

    [Fact(DisplayName = "IRequest.Context takes a context of the request's context type, or null, and rejects any other")]
    public void Context_setter_checks_the_type()
    {
        IRequest query = new TenantProbeQuery();

        query.Context = new TenantProbeContext { Tenant = "t" };
        query.Context.Should().BeOfType<TenantProbeContext>();

        query.Context = null;
        query.Context.Should().BeNull();

        var act = () => query.Context = new RequestContextBase();
        act.Should().Throw<ArgumentException>().WithMessage($"*{typeof(TenantProbeContext).FullName}*{typeof(RequestContextBase).FullName}*");
    }

    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

public sealed class TenantProbeContext : RequestContextBase
{
    public required string Tenant { get; init; }
}

public sealed class TenantProbeContextFactory : IRequestContextFactory<TenantProbeContext>
{
    public const string Tenant = "from-the-application";

    public ValueTask<TenantProbeContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
        => new(new TenantProbeContext { Tenant = Tenant });
}

public sealed class TenantProbeQuery : QueryBase<string, TenantProbeContext>;

public sealed class TenantProbeQueryHandler : IQueryHandler<TenantProbeQuery, string>
{
    public Task<string> Handle(TenantProbeQuery query, CancellationToken cancellationToken) => Task.FromResult(query.Context!.Tenant);
}

public sealed class TenantProbeStream : StreamRequestBase<string, TenantProbeContext>;

public sealed class TenantProbeStreamHandler : IStreamRequestHandler<TenantProbeStream, string>
{
    public async IAsyncEnumerable<string> Handle(TenantProbeStream request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return request.Context!.Tenant;
    }
}
