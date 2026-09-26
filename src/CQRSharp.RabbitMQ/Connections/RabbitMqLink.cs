using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CQRSharp.RabbitMQ;

/// <summary>
///     One of a transport's connections (the one it publishes on, or the one it consumes on), kept open: opened in the
///     background with a capped back-off while the broker cannot be reached, which never fails the host, and, when the
///     transport owns it, reopened whenever it is lost. The broker blocking it (a resource alarm) is tracked, so a send can
///     be deferred at once instead of waiting out its confirm.
/// </summary>
internal sealed class RabbitMqLink : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);

    private readonly RabbitMqConnectionSource _source;
    private readonly IServiceProvider _services;
    private readonly string _transportName;
    private readonly string _clientProvidedName;
    private readonly TimeSpan _maxDelay;
    private readonly Func<IConnection, CancellationToken, Task>? _onOpened;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _disposing = new();
    private readonly object _gate = new();

    private TaskCompletionSource<IConnection> _opened = NewOpened();
    private IConnection? _connection;
    private Task? _loop;
    private string? _blockedReason;
    private TimeSpan _retryDelay = FirstRetryDelay;
    private bool _everOpened;
    private int _disposed;

    /// <param name="source">Where the connection comes from.</param>
    /// <param name="services">The provider the source resolves from.</param>
    /// <param name="transportName">The transport's name, for the logs.</param>
    /// <param name="role">What the connection is for (<c>publish</c>, <c>consume</c>), for the logs and its client name.</param>
    /// <param name="clientProvidedName">The name the broker shows for the connection.</param>
    /// <param name="maxDelay">The longest wait between two attempts to open it.</param>
    /// <param name="onOpened">Runs on every connection it opens before anyone else uses it (the topology the role needs).</param>
    /// <param name="logger">Where the connection's life is logged.</param>
    /// <param name="timeProvider">The clock the back-off waits on.</param>
    public RabbitMqLink(
        RabbitMqConnectionSource source,
        IServiceProvider services,
        string transportName,
        string role,
        string clientProvidedName,
        TimeSpan maxDelay,
        Func<IConnection, CancellationToken, Task>? onOpened,
        ILogger logger,
        TimeProvider timeProvider)
    {
        _source = source;
        _services = services;
        _transportName = transportName;
        Role = role;
        _clientProvidedName = clientProvidedName;
        _maxDelay = maxDelay;
        _onOpened = onOpened;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>What the connection is for: <c>publish</c> or <c>consume</c>.</summary>
    public string Role { get; }

    /// <summary>Whether anything asked for the connection yet.</summary>
    public bool Started
    {
        get
        {
            lock (_gate) return _loop is not null;
        }
    }

    /// <summary>The open connection, or <see langword="null" /> while there is none.</summary>
    public IConnection? Current
    {
        get
        {
            lock (_gate) return _connection is { IsOpen: true } connection ? connection : null;
        }
    }

    /// <summary>Why the broker blocked the connection, while it does.</summary>
    public string? BlockedReason
    {
        get
        {
            lock (_gate) return _blockedReason;
        }
    }

    /// <summary>How long until the next attempt to open the connection is worth waiting for, while it is closed.</summary>
    public TimeSpan RetryDelay
    {
        get
        {
            lock (_gate) return _retryDelay;
        }
    }

    /// <summary>Starts keeping the connection open, once; later calls do nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _disposed != 0) return;
            _loop = _source.Owned ? KeepOwnedOpenAsync(_disposing.Token) : WatchGivenAsync(_disposing.Token);
        }
    }

    /// <summary>The open connection, as soon as there is one.</summary>
    public Task<IConnection> WaitAsync(CancellationToken cancellationToken)
    {
        Start();
        Task<IConnection> opened;
        lock (_gate) opened = _connection is { IsOpen: true } connection ? Task.FromResult(connection) : _opened.Task;
        return opened.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Task? loop;
        lock (_gate) loop = _loop;
        await _disposing.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The loop's own way of ending.
            }

        _disposing.Dispose();
    }

    // For a provider disposed synchronously: the loop is told to stop, and the connection it holds, when owned, is closed
    // here; the loop then ends on its own.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _disposing.Cancel();
        IConnection? connection;
        lock (_gate) connection = _connection;
        if (_source.Owned && connection is not null)
            try
            {
                connection.Dispose();
            }
            catch (Exception)
            {
                // A connection that is already gone has nothing left to close.
            }
    }

    // Opens the connection, then waits for it to be lost and opens another, until the provider is disposed; closes the
    // last one then. A connection that is lost is closed before the next is opened, so a factory whose connections
    // recover by themselves never leaves a second one running beside the transport's.
    private async Task KeepOwnedOpenAsync(CancellationToken disposing)
    {
        // Leave the caller (a send, a hosted service starting) at once: the first attempt may take as long as the connect
        // timeout of an unreachable broker.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        while (!disposing.IsCancellationRequested)
        {
            IConnection connection;
            try
            {
                connection = await _source.OpenAsync(_services, _clientProvidedName, disposing).ConfigureAwait(false);
            }
            catch (Exception ex) when (!disposing.IsCancellationRequested)
            {
                var delay = RetryDelay;
                RabbitMqLog.ConnectFailed(_logger, ex, _transportName, Role, delay);
                await Task.Delay(delay, _timeProvider, disposing).ConfigureAwait(false);
                lock (_gate) _retryDelay = Grow(delay);
                continue;
            }

            var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Watch(connection, reason => lost.TrySetResult(reason), recovered: null);
            // Lost before the watch began: the event that would have said so has already fired.
            if (!connection.IsOpen)
                lost.TrySetResult(connection.CloseReason is { } closed ? Describe(closed) : "closed while opening");

            Exception? setupFailure = null;
            try
            {
                if (_onOpened is not null)
                    await _onOpened(connection, disposing).ConfigureAwait(false);

                Opened(connection);
                var reason = await lost.Task.WaitAsync(disposing).ConfigureAwait(false);
                Closed();
                RabbitMqLog.ConnectionLost(_logger, _transportName, Role, reason);
            }
            catch (OperationCanceledException) when (disposing.IsCancellationRequested)
            {
                Closed();
            }
            catch (Exception ex)
            {
                Closed();
                setupFailure = ex;
            }
            finally
            {
                await CloseQuietlyAsync(connection).ConfigureAwait(false);
            }

            if (setupFailure is not null)
            {
                var delay = RetryDelay;
                RabbitMqLog.ConnectFailed(_logger, setupFailure, _transportName, Role, delay);
                await Task.Delay(delay, _timeProvider, disposing).ConfigureAwait(false);
                lock (_gate) _retryDelay = Grow(delay);
            }
        }
    }

    // The application's connection: resolved once (retried while its factory fails), then only watched. It recovers by
    // itself or not at all, since only its owner may close it.
    private async Task WatchGivenAsync(CancellationToken disposing)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        IConnection connection;
        while (true)
        {
            try
            {
                connection = await _source.OpenAsync(_services, _clientProvidedName, disposing).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (!disposing.IsCancellationRequested)
            {
                var delay = RetryDelay;
                RabbitMqLog.ConnectFailed(_logger, ex, _transportName, Role, delay);
                await Task.Delay(delay, _timeProvider, disposing).ConfigureAwait(false);
                lock (_gate) _retryDelay = Grow(delay);
            }
        }

        Watch(connection, reason =>
        {
            Closed();
            RabbitMqLog.ConnectionLost(_logger, _transportName, Role, reason);
        }, recovered: () => Opened(connection));

        if (connection.IsOpen)
            Opened(connection);
    }

    private void Watch(IConnection connection, Action<string> lost, Action? recovered)
    {
        connection.ConnectionShutdownAsync += (_, e) =>
        {
            // Closed by this transport (at disposal): not a loss.
            if (e.Initiator != ShutdownInitiator.Application) lost(Describe(e));
            return Task.CompletedTask;
        };
        connection.ConnectionBlockedAsync += (_, e) =>
        {
            lock (_gate) _blockedReason = e.Reason;
            RabbitMqLog.Blocked(_logger, _transportName, Role, e.Reason);
            return Task.CompletedTask;
        };
        connection.ConnectionUnblockedAsync += (_, _) =>
        {
            lock (_gate) _blockedReason = null;
            RabbitMqLog.Unblocked(_logger, _transportName, Role);
            return Task.CompletedTask;
        };
        if (recovered is not null)
            connection.RecoverySucceededAsync += (_, _) =>
            {
                recovered();
                return Task.CompletedTask;
            };
    }

    private void Opened(IConnection connection)
    {
        bool reopened;
        TaskCompletionSource<IConnection> opened;
        lock (_gate)
        {
            _connection = connection;
            _blockedReason = null;
            _retryDelay = FirstRetryDelay;
            reopened = _everOpened;
            _everOpened = true;
            opened = _opened;
        }

        opened.TrySetResult(connection);
        var endpoint = connection.Endpoint.ToString();
        if (reopened) RabbitMqLog.Reconnected(_logger, _transportName, Role, endpoint);
        else RabbitMqLog.Connected(_logger, _transportName, Role, endpoint);
    }

    private void Closed()
    {
        lock (_gate)
        {
            _connection = null;
            _blockedReason = null;
            if (_opened.Task.IsCompleted) _opened = NewOpened();
        }
    }

    private TimeSpan Grow(TimeSpan delay) => delay * 2 < _maxDelay ? delay * 2 : _maxDelay;

    private static async Task CloseQuietlyAsync(IConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A connection that is already gone has nothing left to close.
        }
    }

    private static TaskCompletionSource<IConnection> NewOpened() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A shutdown's reason in a line: who closed it, the AMQP code and text.</summary>
    public static string Describe(ShutdownEventArgs e) => $"{e.Initiator}: {e.ReplyCode} {e.ReplyText}";
}
