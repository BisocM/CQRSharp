using CQRSharp.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>The EF Core outbox checks its context's model at host start rather than dead-lettering every message later.</summary>
public sealed class EfCoreOutboxModelValidationTests
{
    [Fact(DisplayName = "Host start fails, naming ApplyCqrsOutbox, when the context maps the outbox but not the inbox")]
    public async Task Host_start_fails_without_the_inbox_mapping()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<OutboxOnlyDbContext>(o => o.UseSqlite(connection));
                services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseEntityFrameworkCore<OutboxOnlyDbContext>()));
            })
            .Build();

        var act = () => host.StartAsync();

        (await act.Should().ThrowAsync<OptionsValidationException>()).WithMessage("*ApplyCqrsOutbox*");
    }

    [Fact(DisplayName = "Host start succeeds when the context applies the CQRSharp outbox mapping")]
    public async Task Host_start_succeeds_with_the_full_mapping()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<FullDbContext>(o => o.UseSqlite(connection));
                services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseEntityFrameworkCore<FullDbContext>()));
            })
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class OutboxOnlyDbContext(DbContextOptions<OutboxOnlyDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new OutboxEntityConfiguration());
    }

    private sealed class FullDbContext(DbContextOptions<FullDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyCqrsOutbox();
    }
}
