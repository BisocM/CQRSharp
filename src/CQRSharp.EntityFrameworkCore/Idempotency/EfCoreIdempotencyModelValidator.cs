using CQRSharp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     Fails host start when <typeparamref name="TContext" /> cannot honour the idempotency store contract: the table is
///     not mapped (every claim would throw), or - on SQL Server, whose default collation folds case - the key column has
///     no case-sensitive collation, so two keys differing only in case would be one request.
/// </summary>
internal sealed class EfCoreIdempotencyModelValidator<TContext>(IServiceScopeFactory scopeFactory, EfCoreStoreSelection selection)
    : IValidateOptions<EfCoreIdempotencyStoreOptions>
    where TContext : DbContext
{
    // The provider's documented name (DatabaseFacade.ProviderName); no dependency on the provider package needed.
    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";

    public ValidateOptionsResult Validate(string? name, EfCoreIdempotencyStoreOptions options)
    {
        // A later registration replaced this store: its context is not used, so it has nothing to honour.
        if (!selection.Selects<IIdempotencyStore, EfCoreIdempotencyStore<TContext>>()) return ValidateOptionsResult.Skip;

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetService<TContext>();
        if (context is null)
            return ValidateOptionsResult.Fail(
                $"{typeof(TContext).Name} is not registered. Call AddDbContext<{typeof(TContext).Name}>() before using it as the EF Core idempotency store.");

        var entity = context.Model.FindEntityType(typeof(IdempotencyEntity));
        if (entity is null)
            return ValidateOptionsResult.Fail(
                $"{typeof(TContext).Name} does not map {nameof(IdempotencyEntity)}. Call modelBuilder.ApplyCqrsIdempotency() in OnModelCreating.");

        if (context.Database.ProviderName == SqlServerProvider)
        {
            // Collation is a design-time facet: the read-optimized runtime model drops it, so it is read from the design-time
            // model EF builds on request (once, here at host start).
            var designTimeModel = context.GetService<IDesignTimeModel>().Model;
            var collation = designTimeModel.FindEntityType(typeof(IdempotencyEntity))?
                .FindProperty(nameof(IdempotencyEntity.Key))?
                .GetCollation();
            if (!IsCaseSensitiveSqlServerCollation(collation))
                return ValidateOptionsResult.Fail(
                    $"The idempotency key column of {typeof(TContext).Name} " +
                    (collation is null ? "uses the database's default collation" : $"uses collation '{collation}'") +
                    ", which on SQL Server may fold case - but idempotency keys are compared ordinally and case-sensitively. " +
                    $"Call modelBuilder.ApplyCqrsIdempotency(IdempotencyEntityConfiguration.SqlServerBinaryCollation) (or another " +
                    "binary or case-sensitive collation) and add a migration.");
        }

        return ValidateOptionsResult.Success;
    }

    // SQL Server names every collation by its comparison rules: binary ones carry _BIN / _BIN2, case-sensitive ones _CS.
    private static bool IsCaseSensitiveSqlServerCollation(string? collation)
        => collation is not null &&
           (collation.Contains("_BIN", StringComparison.OrdinalIgnoreCase) || collation.Contains("_CS", StringComparison.OrdinalIgnoreCase));
}
