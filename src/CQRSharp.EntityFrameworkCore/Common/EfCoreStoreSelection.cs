using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     Which store the application's registrations finally chose, read from the service collection the EF Core store
///     verbs registered into rather than from the container: a store registered after an EF Core one replaces it, and
///     the EF Core store's retention and model check then stand aside without constructing the replacement (a Redis store
///     opening its connection, say) just to learn its type.
/// </summary>
/// <remarks>Asked once the container is built, when the collection no longer changes.</remarks>
internal sealed class EfCoreStoreSelection(IServiceCollection services)
{
    /// <summary>Whether the last registration of <typeparamref name="TService" /> is <typeparamref name="TImplementation" />.</summary>
    public bool Selects<TService, TImplementation>() where TImplementation : TService
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(TService) || descriptor.IsKeyedService) continue;
            return descriptor.ImplementationType == typeof(TImplementation);
        }

        return false;
    }
}
