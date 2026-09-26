using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     The EF Core stores as an application gets them: registered by their verbs over a context registered with
///     <c>AddDbContext</c>, resolved from a container that validates scopes and the whole graph on build.
/// </summary>
internal static class EfCoreStoreServices
{
    /// <summary>A container holding the EF Core idempotency store over <typeparamref name="TContext" />, with its schema created.</summary>
    public static async Task<ServiceProvider> IdempotencyAsync<TContext>(
        Action<DbContextOptionsBuilder> configureContext,
        TimeProvider time,
        TimeSpan retention)
        where TContext : DbContext
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(time);
        services.AddDbContext<TContext>(configureContext);
        services.AddEntityFrameworkCoreIdempotencyStore<TContext>(o => o.Retention = retention);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<TContext>().Database.EnsureCreatedAsync();
        return provider;
    }
}
