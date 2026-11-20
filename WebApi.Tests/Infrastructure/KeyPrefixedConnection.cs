using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using WebApi.Redis;

namespace WebApi.Tests.Infrastructure;

public class KeyPrefixedConnection : DispatchProxy
{
    private IConnectionMultiplexer _inner = null!;

    private IConnectionMultiplexer? _broken;

    private volatile IConnectionMultiplexer _current = null!;

    private RedisKey _prefix;

    public static IConnectionMultiplexer Wrap(IConnectionMultiplexer inner, string prefix)
    {
        var proxy = Create<IConnectionMultiplexer, KeyPrefixedConnection>();
        var connection = (KeyPrefixedConnection)(object)proxy;

        connection._inner = inner;
        connection._current = inner;
        connection._prefix = prefix;

        return proxy;
    }

    public static KeyPrefixedConnection Of(IConnectionMultiplexer connection)
    {
        return (KeyPrefixedConnection)(object)connection;
    }

    public static string ClosedEndpoint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return $"127.0.0.1:{port},connectTimeout=200";
    }

    public void Break()
    {
        lock (this)
        {
            _broken ??= RedisConnection.Connect(ClosedEndpoint());
            _current = _broken;
        }
    }

    public void Restore()
    {
        _current = _inner;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        var disposing = targetMethod.Name is nameof(IDisposable.Dispose) or nameof(IAsyncDisposable.DisposeAsync);

        if (disposing)
        {
            _broken?.Dispose();
        }

        object? result;

        try
        {
            result = targetMethod.Invoke(disposing ? _inner : _current, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }

        return result is IDatabase database && targetMethod.Name == nameof(IConnectionMultiplexer.GetDatabase)
            ? database.WithKeyPrefix(_prefix)
            : result;
    }
}
