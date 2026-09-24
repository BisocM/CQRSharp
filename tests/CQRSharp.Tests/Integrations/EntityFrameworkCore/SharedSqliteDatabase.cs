using Microsoft.Data.Sqlite;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     A named, shared-cache, in-memory SQLite database that lives as long as this object: every context opened on
///     <see cref="ConnectionString" /> sees the same rows over a connection of its own, the way separate processes share a
///     database. A single <see cref="SqliteConnection" /> serves one command at a time, so contexts that run at the same
///     time cannot share one.
/// </summary>
internal sealed class SharedSqliteDatabase : IAsyncDisposable
{
    // An in-memory database exists while a connection to it is open.
    private readonly SqliteConnection _keepAlive;

    public SharedSqliteDatabase()
    {
        ConnectionString = $"Data Source=cqrsharp-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();
    }

    public string ConnectionString { get; }

    public ValueTask DisposeAsync() => _keepAlive.DisposeAsync();
}
