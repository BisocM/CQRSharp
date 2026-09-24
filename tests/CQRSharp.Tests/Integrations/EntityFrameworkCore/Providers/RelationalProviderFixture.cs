using CQRSharp.EntityFrameworkCore;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore.Providers;

/// <summary>
///     A minimal context mapping every CQRSharp table, so the EF Core stores can be exercised on a real provider.
/// </summary>
public sealed class ProviderTestDbContext(DbContextOptions<ProviderTestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new OutboxEntityConfiguration());
        modelBuilder.ApplyConfiguration(new InboxEntityConfiguration());
        // What an application on SQL Server writes: the key column compares ordinally there too.
        modelBuilder.ApplyConfiguration(new IdempotencyEntityConfiguration(
            Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer" ? IdempotencyEntityConfiguration.SqlServerBinaryCollation : null));
    }
}

/// <summary>
///     One real database per provider for the whole test collection. The server comes from an environment variable when
///     CI provides a service container, otherwise from a Testcontainers container started here; when neither is possible
///     (no Docker), <see cref="Available" /> stays false and the provider tests skip with a reason.
/// </summary>
/// <remarks>
///     <c>dotnet test</c> runs the net8.0, net9.0 and net10.0 test processes at the same time, so they must not share
///     tables: on a server named by the environment each process creates and owns the database
///     <c>&lt;database&gt;_net&lt;major&gt;</c> (the account needs the right to create databases), dropping any copy an
///     earlier run left so the schema is always the current model's. A Testcontainers container is private to its
///     process already. Inside the process the collection runs its tests sequentially, and each store resets the tables
///     it uses.
/// </remarks>
public abstract class RelationalProviderFixture : IAsyncLifetime
{
    private IContainer? _container;

    public bool Available { get; private set; }

    public string SkipReason { get; private set; } = "The provider database was not started.";

    public string ConnectionString { get; private set; } = string.Empty;

    protected abstract string EnvironmentVariable { get; }

    protected abstract IContainer BuildContainer();

    protected abstract string ConnectionStringOf(IContainer container);

    /// <summary>The connection string with <paramref name="suffix" /> appended to its database name.</summary>
    protected abstract string WithDatabaseSuffix(string connectionString, string suffix);

    /// <summary>
    ///     The database this process owns on a server the test processes share: the configured one suffixed with the
    ///     runtime's major version, which differs between the concurrently running target frameworks.
    /// </summary>
    internal string OwnDatabaseOn(string sharedServer) => WithDatabaseSuffix(sharedServer, $"net{Environment.Version.Major}");

    /// <summary>Points <paramref name="builder" /> at this fixture's database: what an application's <c>AddDbContext</c> call does.</summary>
    public abstract void Configure(DbContextOptionsBuilder builder);

    public DbContextOptions<ProviderTestDbContext> CreateOptions()
    {
        var builder = new DbContextOptionsBuilder<ProviderTestDbContext>();
        Configure(builder);
        return builder.Options;
    }

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var sharedServer = false;
        if (!string.IsNullOrWhiteSpace(external))
        {
            sharedServer = true;
            ConnectionString = OwnDatabaseOn(external);
        }
        else
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                _container = BuildContainer();
                await _container.StartAsync(timeout.Token);
                ConnectionString = ConnectionStringOf(_container);
            }
            catch (Exception ex)
            {
                SkipReason = $"No {GetType().Name.Replace("Fixture", string.Empty)} database: set {EnvironmentVariable} to a connection string or make Docker available for Testcontainers ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}).";
                return;
            }
        }

        await using var context = new ProviderTestDbContext(CreateOptions());
        // EnsureCreated keeps an existing database whatever its schema, so this process's database from an earlier run
        // is dropped first.
        if (sharedServer)
            await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        Available = true;
    }

    /// <summary>Empties every CQRSharp table so a store starts from nothing.</summary>
    public async Task ResetAsync()
    {
        await using var context = new ProviderTestDbContext(CreateOptions());
        await context.Set<OutboxEntity>().ExecuteDeleteAsync();
        await context.Set<InboxEntity>().ExecuteDeleteAsync();
        await context.Set<IdempotencyEntity>().ExecuteDeleteAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

public sealed class PostgreSqlFixture : RelationalProviderFixture
{
    protected override string EnvironmentVariable => "CQRSHARP_TEST_POSTGRES";

    protected override IContainer BuildContainer() => new PostgreSqlBuilder("postgres:16-alpine").Build();

    protected override string ConnectionStringOf(IContainer container) => ((PostgreSqlContainer)container).GetConnectionString();

    protected override string WithDatabaseSuffix(string connectionString, string suffix)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        builder.Database = $"{(string.IsNullOrEmpty(builder.Database) ? "cqrsharp" : builder.Database)}_{suffix}";
        return builder.ConnectionString;
    }

    public override void Configure(DbContextOptionsBuilder builder) => builder.UseNpgsql(ConnectionString);
}

public sealed class SqlServerFixture : RelationalProviderFixture
{
    protected override string EnvironmentVariable => "CQRSHARP_TEST_SQLSERVER";

    protected override IContainer BuildContainer() => new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    protected override string ConnectionStringOf(IContainer container) => ((MsSqlContainer)container).GetConnectionString();

    protected override string WithDatabaseSuffix(string connectionString, string suffix)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        builder.InitialCatalog = $"{(string.IsNullOrEmpty(builder.InitialCatalog) ? "cqrsharp" : builder.InitialCatalog)}_{suffix}";
        return builder.ConnectionString;
    }

    public override void Configure(DbContextOptionsBuilder builder) => builder.UseSqlServer(ConnectionString);
}

[CollectionDefinition(PostgreSqlCollection.Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL";
}

[CollectionDefinition(SqlServerCollection.Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
