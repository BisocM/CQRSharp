using CQRSharp.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     The EF Core idempotency store checks its context's model at host start: on SQL Server the key column must
///     compare case-sensitively.
/// </summary>
public sealed class EfCoreIdempotencyModelValidationTests
{
    [Fact(DisplayName = "EF Core on SQL Server: host start fails when the idempotency key column may fold case")]
    public void SqlServer_key_column_without_a_case_sensitive_collation_fails_start()
    {
        var act = () => IdempotencyOptions<CaseFoldingContext>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*SqlServerBinaryCollation*");
    }

    [Fact(DisplayName = "EF Core on SQL Server: a binary key collation passes the start check")]
    public void SqlServer_key_column_with_a_binary_collation_passes()
    {
        var act = () => IdempotencyOptions<BinaryKeyContext>().Value;

        act.Should().NotThrow();
    }

    private static IOptions<EfCoreIdempotencyStoreOptions> IdempotencyOptions<TContext>() where TContext : DbContext
    {
        var services = new ServiceCollection();
        // SQL Server as the provider; nothing connects - the check reads the model.
        services.AddDbContext<TContext>(o => o.UseSqlServer("Server=unused;Database=unused;TrustServerCertificate=true"));
        services.AddLogging();
        services.AddEntityFrameworkCoreIdempotencyStore<TContext>();
        return services.BuildServiceProvider().GetRequiredService<IOptions<EfCoreIdempotencyStoreOptions>>();
    }

    private sealed class CaseFoldingContext(DbContextOptions<CaseFoldingContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCqrsIdempotency();
    }

    private sealed class BinaryKeyContext(DbContextOptions<BinaryKeyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyCqrsIdempotency(IdempotencyEntityConfiguration.SqlServerBinaryCollation);
    }
}
