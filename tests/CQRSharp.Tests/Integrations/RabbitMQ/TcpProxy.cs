using System.Net;
using System.Net.Sockets;

namespace CQRSharp.Tests.Integrations.RabbitMQ;

/// <summary>
///     A TCP forwarder between the transport under test and the broker, which a test cuts to stand for an outage (every
///     connection through it is dropped, and new ones are refused) and restores. It works with any broker, a CI service
///     container included, which a test could not stop.
/// </summary>
public sealed class TcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _open = [];
    private readonly object _gate = new();
    private readonly Task _accepting;
    private volatile bool _cut;

    private TcpProxy(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accepting = AcceptAsync();
    }

    /// <summary>The local port the proxy listens on.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static TcpProxy Start(string targetHost, int targetPort) => new(targetHost, targetPort);

    /// <summary>Drops every connection through the proxy and refuses new ones until <see cref="Restore" />.</summary>
    public void Cut()
    {
        _cut = true;
        lock (_gate)
        {
            foreach (var client in _open) client.Dispose();
            _open.Clear();
        }
    }

    /// <summary>Accepts connections again.</summary>
    public void Restore() => _cut = false;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Cut();
        try
        {
            await _accepting;
        }
        catch (Exception)
        {
            // Stopping the listener ends the accept loop with an exception.
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            if (_cut)
            {
                client.Dispose();
                continue;
            }

            _ = ForwardAsync(client);
        }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        var upstream = new TcpClient();
        lock (_gate)
        {
            _open.Add(client);
            _open.Add(upstream);
        }

        try
        {
            await upstream.ConnectAsync(_targetHost, _targetPort, _stop.Token);
            var downstreamStream = client.GetStream();
            var upstreamStream = upstream.GetStream();
            await Task.WhenAny(
                downstreamStream.CopyToAsync(upstreamStream, _stop.Token),
                upstreamStream.CopyToAsync(downstreamStream, _stop.Token));
        }
        catch (Exception)
        {
            // A cut, a stop, or either side closing: the pair is done either way.
        }
        finally
        {
            lock (_gate)
            {
                _open.Remove(client);
                _open.Remove(upstream);
            }

            client.Dispose();
            upstream.Dispose();
        }
    }
}
