using System.Diagnostics.CodeAnalysis;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.Pipelines;
using Microsoft.EntityFrameworkCore;

// Namespace-extends the DI builder so the fluent verb reads naturally next to the rest of the app's wiring.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     The fluent unit-of-work verb of the EF Core integration: the builder's <c>UseUnitOfWork(...)</c> over an
///     <see cref="EfCoreUnitOfWork{TContext}" /> for the already-registered <c>DbContext</c> (register it with
///     <c>AddDbContext&lt;TContext&gt;</c> yourself).
/// </summary>
public static class EntityFrameworkCoreUnitOfWorkBuilderExtensions
{
    /// <summary>
    ///     Runs transactional requests (<c>ITransactionalCommand</c>, <c>ITransactionalQuery</c>) in a transaction of the
    ///     scoped <typeparamref name="TContext" />, through <see cref="EfCoreUnitOfWork{TContext}" />. With the EF Core
    ///     outbox store over the same context, a request's outbox messages commit with its changes; with the EF Core inbox,
    ///     a delivery's record commits with its handler's changes.
    /// </summary>
    /// <typeparam name="TContext">The application's <see cref="DbContext" />, registered scoped.</typeparam>
    /// <param name="builder">The CQRSharp builder.</param>
    /// <param name="configure">Optional unit-of-work options (the default isolation level, failed-result handling).</param>
    /// <returns>The same <paramref name="builder" /> for chaining.</returns>
    [RequiresDynamicCode(EfCoreAot.Message)]
    [RequiresUnreferencedCode(EfCoreAot.Message)]
    public static ICqrsBuilder UseEntityFrameworkCoreUnitOfWork<TContext>(
        this ICqrsBuilder builder,
        Action<UnitOfWorkOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseUnitOfWork(sp => new EfCoreUnitOfWork<TContext>(sp.GetRequiredService<TContext>()), configure);
    }
}
