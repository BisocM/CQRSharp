using Microsoft.Data.Sqlite;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     A SQLite database in a temporary file that lives as long as this object. For the tests that read through contexts
///     of their own while another context's transaction holds uncommitted writes (a unit of work still open, a processor
///     delivering): <see cref="SharedSqliteDatabase" />'s shared cache locks whole tables, so such a read fails at once
///     with SQLITE_LOCKED instead of seeing the committed rows, while a file database in rollback-journal mode serves it.
/// </summary>
internal sealed class SqliteFileDatabase : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cqrsharp-{Guid.NewGuid():N}.db");

    public string ConnectionString => $"Data Source={_path}";

    public ValueTask DisposeAsync()
    {
        // Only this database's pooled connections: they keep the file open, and other tests' pools are theirs.
        using (var connection = new SqliteConnection(ConnectionString))
            SqliteConnection.ClearPool(connection);

        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
            File.Delete(_path + suffix);
        return ValueTask.CompletedTask;
    }
}
