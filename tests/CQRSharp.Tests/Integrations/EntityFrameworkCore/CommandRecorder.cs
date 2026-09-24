using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>Records the SQL of every command a context sends, and the rows each non-query changed.</summary>
internal sealed class CommandRecorder : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly ConcurrentQueue<(string Sql, int Rows)> _nonQueries = new();

    /// <summary>Every command sent since the last <see cref="Clear" />, in order.</summary>
    public IReadOnlyList<string> Commands => _commands.ToArray();

    /// <summary>Every non-query (INSERT, UPDATE, DELETE) since the last <see cref="Clear" />, with the rows it changed.</summary>
    public IReadOnlyList<(string Sql, int Rows)> NonQueries => _nonQueries.ToArray();

    public void Clear()
    {
        _commands.Clear();
        _nonQueries.Clear();
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText.Trim());
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText.Trim());
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        _nonQueries.Enqueue((command.CommandText.Trim(), result));
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText.Trim());
        return ValueTask.FromResult(result);
    }
}
