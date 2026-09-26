using StackExchange.Redis;

namespace CQRSharp.Redis;

/// <summary>
///     The connections CQRSharp.Redis opens itself, for the stores registered with a connection string: one per distinct
///     string (compared ordinally), so stores given the same string share a connection and stores given different ones
///     each get their own. One pool per service provider, created and disposed by it; disposing it closes exactly the
///     connections it opened, never one the application passed in.
/// </summary>
internal sealed class RedisConnectionPool : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<string, ConnectionMultiplexer> _connections = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>The connection for <paramref name="connectionString" />, opened on first use.</summary>
    public IConnectionMultiplexer Get(string connectionString)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Only a connection that opened is kept: a failed Connect (abortConnect=true while the server is down) throws
            // to this resolution and the next one tries again, instead of the failure being kept for the provider's life.
            if (!_connections.TryGetValue(connectionString, out var connection))
            {
                connection = ConnectionMultiplexer.Connect(connectionString);
                _connections.Add(connectionString, connection);
            }

            return connection;
        }
    }

    public void Dispose()
    {
        foreach (var connection in TakeAll())
            connection.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in TakeAll())
            await connection.DisposeAsync().ConfigureAwait(false);
    }

    private ConnectionMultiplexer[] TakeAll()
    {
        lock (_gate)
        {
            if (_disposed) return [];

            _disposed = true;
            ConnectionMultiplexer[] connections = [.. _connections.Values];
            _connections.Clear();
            return connections;
        }
    }
}
