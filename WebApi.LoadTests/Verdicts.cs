namespace WebApi.LoadTests;

public sealed record Sample(string Endpoint, long User, long StartMs, long EndMs, int Status);

public sealed record LimitFinding(long User, long StartMs, bool Definite);

public static class RateLimitDetector
{
    public const int Limit = 60;
    public const long WindowMs = 60_000;

    public static List<LimitFinding> Find(IEnumerable<Sample> samples)
    {
        var findings = new List<LimitFinding>();
        foreach (var user in samples.Where(x => x.Status != 429).GroupBy(x => x.User))
        {
            var passed = user.OrderBy(x => x.StartMs).ToList();
            for (var i = 0; i + Limit < passed.Count; i++)
            {
                var first = passed[i];
                var last = passed[i + Limit];
                if (last.EndMs - first.StartMs < WindowMs)
                {
                    findings.Add(new LimitFinding(user.Key, first.StartMs, true));
                }
                else if (last.StartMs - first.EndMs < WindowMs)
                {
                    findings.Add(new LimitFinding(user.Key, first.StartMs, false));
                }
            }
        }

        return findings;
    }
}

public static class Recovery
{
    // решение: восстановление — p95 после подъёма Redis не больше 1,5× от p95 до остановки, 1005 стихает за 10 с (29.4).
    public const double LatencyFactor = 1.5;
    public static readonly TimeSpan EventGrace = TimeSpan.FromSeconds(10);

    public static bool LatencyRecovered(double p95Before, double p95After)
    {
        return p95After <= p95Before * LatencyFactor;
    }

    public static bool EventsStopped(DateTimeOffset? lastEvent, DateTimeOffset redisStarted)
    {
        return lastEvent is null || lastEvent <= redisStarted + EventGrace;
    }
}

public static class Stats
{
    public static double Percentile(IReadOnlyList<double> sorted, double percent)
    {
        return sorted[Math.Max(0, (int)Math.Ceiling(percent / 100 * sorted.Count) - 1)];
    }
}
