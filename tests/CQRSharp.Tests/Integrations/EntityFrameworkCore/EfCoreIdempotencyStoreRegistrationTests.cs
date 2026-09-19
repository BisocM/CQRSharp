using CQRSharp.Pipelines;
using CQRSharp.EntityFrameworkCore.Extensions;
using CQRSharp.EntityFrameworkCore.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     The contract suite constructs the store directly, so it could not see how DI wires it. The store used to be a
///     singleton injected with the (scoped) <c>DbContext</c> — a captive dependency that fails provider validation (the
///     ASP.NET Core Development default) and otherwise pins one root context for the process lifetime. It must resolve
///     under full validation and must not track claims on a long-lived context.
/// </summary>
public sealed class EfCoreIdempotencyStoreRegistrationTests
{
    [Fact(DisplayName = "The DI-registered EF idempotency store resolves under scope validation and claims through a fresh context")]
    public async Task Store_resolves_under_validation_and_claims()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<RegistrationDbContext>(o => o.UseSqlite(connection));
        services.AddEntityFrameworkCoreIdempotencyStore<RegistrationDbContext>();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        await using (var setup = provider.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<RegistrationDbContext>().Database.EnsureCreatedAsync();

        var store = provider.GetRequiredService<IIdempotencyStore>();

        (await store.TryClaimAsync("order-42", CancellationToken.None)).IsClaimed.Should().BeTrue();
        (await store.TryClaimAsync("order-42", CancellationToken.None)).IsClaimed.Should().BeFalse("the key is already claimed");

        await store.ReleaseAsync("order-42", CancellationToken.None);
        (await store.TryClaimAsync("order-42", CancellationToken.None)).IsClaimed.Should().BeTrue("a released key is claimable again");
    }

    private sealed class RegistrationDbContext(DbContextOptions<RegistrationDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new IdempotencyEntityConfiguration());
    }
}
