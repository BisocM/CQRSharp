using CQRSharp.Core.Registries;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Which factory creates a context type's contexts when several are registered: the application's own always, before
///     or after the generated registrations; otherwise the discovered factory registered last, which is the composition
///     root's, since its module registers after the modules it references, and a later <c>AddCqrsGenerated</c> after an
///     earlier one. Never the first discovered factory by accident of registration order.
/// </summary>
public sealed class DiscoveredContextFactoriesTests
{
    [Fact(DisplayName = "A discovered factory registered later replaces an earlier one: the composition root's wins over a library's")]
    public void A_later_discovered_factory_replaces_an_earlier_one()
    {
        var services = new ServiceCollection();
        DiscoveredContextFactories.Register<FactoryProbeContext, LibraryFactory>(services);
        DiscoveredContextFactories.Register<FactoryProbeContext, HostFactory>(services);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRequestContextFactory<FactoryProbeContext>>().Should().BeOfType<HostFactory>();
        provider.GetServices<IRequestContextFactory<FactoryProbeContext>>().Should().ContainSingle("the replaced factory is not left registered");
    }

    [Theory(DisplayName = "A factory the application registers wins over every discovered one, registered before or after them")]
    [InlineData(true)]
    [InlineData(false)]
    public void The_application_registration_wins(bool registeredFirst)
    {
        var services = new ServiceCollection();
        if (registeredFirst) services.AddTransient<IRequestContextFactory<FactoryProbeContext>, ApplicationFactory>();
        DiscoveredContextFactories.Register<FactoryProbeContext, LibraryFactory>(services);
        DiscoveredContextFactories.Register<FactoryProbeContext, HostFactory>(services);
        if (!registeredFirst) services.AddTransient<IRequestContextFactory<FactoryProbeContext>, ApplicationFactory>();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRequestContextFactory<FactoryProbeContext>>().Should().BeOfType<ApplicationFactory>();
    }

    [Fact(DisplayName = "A discovered factory of the default context replaces the built-in one, even when a later AddCqrsGenerated registers it")]
    public void A_discovered_default_context_factory_replaces_the_built_in_one()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        DiscoveredContextFactories.RegisterDefault(services);
        DiscoveredContextFactories.Register<RequestContextBase, DefaultContextFactory>(services);
        DiscoveredContextFactories.RegisterDefault(services);
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IRequestContextFactory<RequestContextBase>>().Should().ContainSingle()
            .Which.Should().BeOfType<DefaultContextFactory>();
    }

    [Fact(DisplayName = "The built-in default context factory is not added over the application's own")]
    public void The_built_in_factory_does_not_replace_the_application_one()
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestContextFactory<RequestContextBase>, DefaultContextFactory>();
        DiscoveredContextFactories.RegisterDefault(services);
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IRequestContextFactory<RequestContextBase>>().Should().ContainSingle()
            .Which.Should().BeOfType<DefaultContextFactory>();
    }

    // A library's own AddCqrsGenerated composes first, with its factory for the context type; the host's composes after
    // it and brings the host's factory, which must win as the host's handler wins for a request both handle.
    [Fact(DisplayName = "Across two compositions, the one registered later replaces the earlier one's discovered factory")]
    public void A_later_composition_replaces_an_earlier_one()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        DiscoveredContextFactories.Register<FactoryProbeContext, LibraryFactory>(services);
        DiscoveredContextFactories.RegisterDefault(services);
        DiscoveredContextFactories.Register<FactoryProbeContext, HostFactory>(services);
        DiscoveredContextFactories.RegisterDefault(services);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRequestContextFactory<FactoryProbeContext>>().Should().BeOfType<HostFactory>();
        provider.GetServices<IRequestContextFactory<RequestContextBase>>().Should().ContainSingle("the built-in factory is registered once");
    }

    // Registered by hand only: the generator would register them itself (and report two in one assembly, CQRGEN018).
    private sealed class LibraryFactory : IRequestContextFactory<FactoryProbeContext>
    {
        public ValueTask<FactoryProbeContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new FactoryProbeContext());
    }

    private sealed class HostFactory : IRequestContextFactory<FactoryProbeContext>
    {
        public ValueTask<FactoryProbeContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new FactoryProbeContext());
    }

    private sealed class ApplicationFactory : IRequestContextFactory<FactoryProbeContext>
    {
        public ValueTask<FactoryProbeContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new FactoryProbeContext());
    }

    private sealed class DefaultContextFactory : IRequestContextFactory<RequestContextBase>
    {
        public ValueTask<RequestContextBase> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new RequestContextBase());
    }

    private sealed class FactoryProbeContext : RequestContextBase;
}
