using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;

namespace WebApi.Tests.Infrastructure;

public sealed class FreezableRedisProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _sockets = [];
    private readonly ConfigurationOptions _upstream;
    private readonly Task _accepting;
    private volatile bool _frozen;

    private FreezableRedisProxy(string redisConnectionString)
    {
        _upstream = ConfigurationOptions.Parse(redisConnectionString);
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public string ConnectionString
    {
        get
        {
            var options = _upstream.Clone();

            options.EndPoints.Clear();
            options.EndPoints.Add(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);

            return options.ToString();
        }
    }

    public static FreezableRedisProxy Start(string redisConnectionString)
    {
        return new FreezableRedisProxy(redisConnectionString);
    }

    public void Freeze()
    {
        _frozen = true;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();

        lock (_sockets)
        {
            foreach (var socket in _sockets)
            {
                socket.Dispose();
            }
        }

        await _accepting;
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        var (host, port) = _upstream.EndPoints[0] switch
        {
            DnsEndPoint dns => (dns.Host, dns.Port),
            IPEndPoint ip => (ip.Address.ToString(), ip.Port),
            var other => throw new NotSupportedException(other.ToString())
        };

        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var upstream = new TcpClient();

                lock (_sockets)
                {
                    _sockets.Add(client);
                    _sockets.Add(upstream);
                }

                await upstream.ConnectAsync(host, port, _stop.Token);
                _ = PumpAsync(client.GetStream(), upstream.GetStream());
                _ = PumpAsync(upstream.GetStream(), client.GetStream());
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[8192];

        try
        {
            int read;

            while ((read = await from.ReadAsync(buffer, _stop.Token)) > 0)
            {
                if (!_frozen)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }
}
