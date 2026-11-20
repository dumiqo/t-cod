namespace WebApi.RateLimiting;

public sealed class LocalRateJournal
{
    private readonly Dictionary<long, List<long>> _journals = [];
    private readonly Lock _lock = new();
    private long _nextSweep = long.MinValue;

    public int UserCount
    {
        get
        {
            lock (_lock)
            {
                return _journals.Count;
            }
        }
    }

    public bool TryReserve(long userId, long now, out long oldest)
    {
        // решение: один замок на весь журнал — операции по микросекунде, а обход с удалением
        // пользователей не может потерять одновременную бронь.
        lock (_lock)
        {
            if (now >= _nextSweep)
            {
                Sweep(now);
                _nextSweep = now + RateLimitWindow.Milliseconds;
            }

            if (!_journals.TryGetValue(userId, out var moments))
            {
                moments = [];
                _journals[userId] = moments;
            }

            Trim(moments, now);
            if (moments.Count >= RateLimitWindow.Limit)
            {
                oldest = moments.Min();

                return false;
            }

            moments.Add(now);
            oldest = 0;

            return true;
        }
    }

    public void Release(long userId, long now)
    {
        lock (_lock)
        {
            if (_journals.TryGetValue(userId, out var moments))
            {
                moments.Remove(now);
            }
        }
    }

    private void Sweep(long now)
    {
        foreach (var (userId, moments) in _journals)
        {
            Trim(moments, now);
            if (moments.Count == 0)
            {
                _journals.Remove(userId);
            }
        }
    }

    private static void Trim(List<long> moments, long now)
    {
        moments.RemoveAll(x => x <= now - RateLimitWindow.Milliseconds);
    }
}
