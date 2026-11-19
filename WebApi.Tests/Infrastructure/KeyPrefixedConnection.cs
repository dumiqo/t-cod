using System.Reflection;
using System.Runtime.ExceptionServices;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace WebApi.Tests.Infrastructure;

public class KeyPrefixedConnection : DispatchProxy
{
    private IConnectionMultiplexer _inner = null!;

    private RedisKey _prefix;

    public static IConnectionMultiplexer Wrap(IConnectionMultiplexer inner, string prefix)
    {
        var proxy = Create<IConnectionMultiplexer, KeyPrefixedConnection>();
        var connection = (KeyPrefixedConnection)(object)proxy;

        connection._inner = inner;
        connection._prefix = prefix;

        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        object? result;

        try
        {
            result = targetMethod.Invoke(_inner, args);
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
