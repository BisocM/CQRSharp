using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore.Providers;

// dotnet test runs the three target frameworks' test processes at the same time against the one server CI provides; a
// process that emptied or claimed from another's tables made the provider suites fail. Each process owns a database.
public sealed class RelationalProviderFixtureTests
{
    private static readonly string Own = $"net{Environment.Version.Major}";

    [Fact(DisplayName = "On a shared PostgreSQL server, the process owns a database named for its runtime")]
    public void PostgreSql_database_is_per_process()
    {
        var connectionString = new PostgreSqlFixture().OwnDatabaseOn("Host=db;Port=5432;Username=u;Password=p;Database=cqrsharp");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        builder.Database.Should().Be($"cqrsharp_{Own}");
        builder.Host.Should().Be("db");
        builder.Username.Should().Be("u");
    }

    [Fact(DisplayName = "On a shared PostgreSQL server without a database name, the process still owns one")]
    public void PostgreSql_database_defaults_when_unnamed()
    {
        var connectionString = new PostgreSqlFixture().OwnDatabaseOn("Host=db;Username=u;Password=p");

        new NpgsqlConnectionStringBuilder(connectionString).Database.Should().Be($"cqrsharp_{Own}");
    }

    [Fact(DisplayName = "On a shared SQL Server, the process owns a database named for its runtime")]
    public void SqlServer_database_is_per_process()
    {
        var connectionString = new SqlServerFixture().OwnDatabaseOn("Server=db,1433;User Id=sa;Password=p;TrustServerCertificate=true;Database=cqrsharp_tests");

        var builder = new SqlConnectionStringBuilder(connectionString);
        builder.InitialCatalog.Should().Be($"cqrsharp_tests_{Own}");
        builder.DataSource.Should().Be("db,1433");
        builder.UserID.Should().Be("sa");
    }

    [Fact(DisplayName = "On a shared SQL Server without a database name, the process still owns one")]
    public void SqlServer_database_defaults_when_unnamed()
    {
        var connectionString = new SqlServerFixture().OwnDatabaseOn("Server=db;User Id=sa;Password=p");

        new SqlConnectionStringBuilder(connectionString).InitialCatalog.Should().Be($"cqrsharp_{Own}");
    }
}
