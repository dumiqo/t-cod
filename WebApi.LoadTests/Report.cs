using System.Globalization;
using System.Text;
using System.Text.Json;
using WebApi.Tests.Invariants;

namespace WebApi.LoadTests;

public static class Report
{
    // решение: прогрев 30 с и шаг калибровки 60 с (10 с разгон + 50 с полка) — числа из 29.4, в задании их нет.
    private const long WarmupMs = 30_000;
    private const long StepMs = 60_000;
    private const long RampMs = 10_000;
    private const double CalibrationP95Ms = 200;
    private const double WorkingShare = 0.7;
    private const string Greedy = "greedy";

    public static async Task<int> RunAsync(string connection, string outDir, string mode)
    {
        var samples = (await File.ReadAllLinesAsync(Path.Combine(outDir, "requests.csv")))
            .Select(x => x.Split(','))
            .Where(x => x.Length == 6)
            .Select(x => (Sample: new Sample(x[0], long.Parse(x[1]), long.Parse(x[2]), long.Parse(x[3]), int.Parse(x[4])), Duration: double.Parse(x[5], CultureInfo.InvariantCulture)))
            .ToList();
        var begin = samples.Min(x => x.Sample.StartMs);
        var end = samples.Max(x => x.Sample.EndMs);
        var md = new StringBuilder();
        var failures = new List<string>();

        md.AppendLine($"#### {mode}").AppendLine();
        if (mode == "calibrate")
        {
            Calibrate(samples, begin, md);
        }
        else
        {
            var measured = samples.Where(x => x.Sample.StartMs >= begin + WarmupMs).ToList();
            md.AppendLine($"Пропускная способность: {measured.Count / ((end - begin - WarmupMs) / 1000.0):F0} RPS (после прогрева, вместе с «жадными»).").AppendLine();
            Table(measured, md);
        }

        var server = samples.Where(x => x.Sample.Status >= 500).ToList();
        var crashes = server.Count(x => x.Sample.Status == 500);
        if (crashes > 0)
        {
            failures.Add($"{crashes} ответов 500");
        }

        var findings = RateLimitDetector.Find(samples.Select(x => x.Sample));
        var greedy = samples.Where(x => x.Sample.Endpoint == Greedy).ToList();
        md.AppendLine($"Лимит: достоверных нарушений {findings.Count(x => x.Definite)}, пограничных {findings.Count(x => !x.Definite)}; " +
            $"у «жадных» 429 — {Share(greedy, 429)} из {greedy.Count}.");
        if (findings.Any(x => x.Definite))
        {
            failures.Add("лимит нарушен");
        }

        if (greedy.All(x => x.Sample.Status != 429))
        {
            failures.Add("у «жадных» ни одного 429");
        }

        var data = JsonSerializer.Deserialize<LoadData>(await File.ReadAllTextAsync(Path.Combine(outDir, "data.json")), Json.Options)!;
        var violations = await InvariantChecker.CheckAsync(connection, data.Classes, data.InitialBalances);
        md.AppendLine($"Инварианты 29.3: {(violations.Count == 0 ? "выполнены" : string.Join("; ", violations.Take(10)))}.");
        if (violations.Count > 0)
        {
            failures.Add($"{violations.Count} нарушений инвариантов");
        }

        var redisEvents = (await File.ReadAllLinesAsync(Path.Combine(outDir, "app.log")))
            .Where(x => x.StartsWith('{') && x.Contains("\"EventId\":1005"))
            .Select(x => JsonDocument.Parse(x).RootElement.GetProperty("Timestamp").GetDateTimeOffset())
            .ToList();
        var health = (await File.ReadAllLinesAsync(Path.Combine(outDir, "health.csv")))
            .Select(x => x.Split(','))
            .Select(x => (At: long.Parse(x[0]), Status: x[1]))
            .ToList();
        md.AppendLine($"Событий 1005: {redisEvents.Count}; `/health/ready` не Healthy {health.Count(x => x.Status != "Healthy")} из {health.Count} опросов, последний — {health.LastOrDefault().Status}.");

        var events = File.Exists(Path.Combine(outDir, "events.csv"))
            ? (await File.ReadAllLinesAsync(Path.Combine(outDir, "events.csv"))).Select(x => x.Split(',')).ToDictionary(x => x[0], x => long.Parse(x[1]))
            : [];
        if (events.TryGetValue("stop", out var stopped) && events.TryGetValue("start", out var started))
        {
            var main = samples.Where(x => x.Sample.Endpoint != Greedy).ToList();
            var before = P95(main.Where(x => x.Sample.StartMs >= stopped - StepMs && x.Sample.StartMs < stopped));
            var after = P95(main.Where(x => x.Sample.StartMs >= end - StepMs));
            var lastEvent = redisEvents.Count == 0 ? (DateTimeOffset?)null : redisEvents.Max();
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(started);
            var latencyOk = Recovery.LatencyRecovered(before, after);
            var eventsOk = Recovery.EventsStopped(lastEvent, startedAt);
            var healthOk = health.LastOrDefault().Status == "Healthy";
            md.AppendLine($"Восстановление: p95 до остановки {before:F1} мс, за последнюю минуту {after:F1} мс ({(latencyOk ? "≤" : ">")} 1,5×); " +
                $"последнее 1005 через {(lastEvent is null ? "—" : $"{(lastEvent.Value - startedAt).TotalSeconds:F1} с")} после start; ready в конце — {health.LastOrDefault().Status}.");
            if (mode == "restart")
            {
                if (!latencyOk)
                {
                    failures.Add("задержки не вернулись");
                }

                if (!eventsOk)
                {
                    failures.Add("1005 не прекратилось за 10 с");
                }

                if (!healthOk)
                {
                    failures.Add("ready не Healthy");
                }
            }
        }

        md.AppendLine().AppendLine(failures.Count == 0 ? "Итог: пройден." : $"Итог: НЕ пройден — {string.Join(", ", failures)}.");
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), md.ToString());
        Console.WriteLine(md);
        return failures.Count == 0 ? 0 : 1;
    }

    private static void Calibrate(List<(Sample Sample, double Duration)> samples, long begin, StringBuilder md)
    {
        md.AppendLine("| шаг | RPS факт | худший p95, мс | эндпоинт |").AppendLine("|---|---|---|---|");
        var passed = 0.0;
        var failedOnce = false;
        for (var step = 0; ; step++)
        {
            var from = begin + step * StepMs + RampMs;
            var hold = samples.Where(x => x.Sample.Endpoint != Greedy && x.Sample.StartMs >= from && x.Sample.StartMs < begin + (step + 1) * StepMs).ToList();
            if (hold.Count == 0)
            {
                break;
            }

            var worst = hold.GroupBy(x => x.Sample.Endpoint).Select(g => (Endpoint: g.Key, P95: P95(g))).MaxBy(x => x.P95);
            var rps = hold.Count / ((StepMs - RampMs) / 1000.0);
            md.AppendLine($"| {step + 1} | {rps:F0} | {worst.P95:F1} | {worst.Endpoint} |");
            if (worst.P95 > CalibrationP95Ms)
            {
                failedOnce = true;
            }
            else if (!failedOnce)
            {
                passed = rps;
            }
        }

        md.AppendLine().AppendLine($"Наибольшая частота с p95 ≤ {CalibrationP95Ms} мс у каждого эндпоинта: {passed:F0} RPS; рабочая нагрузка (70%): {passed * WorkingShare:F0} RPS.").AppendLine();
    }

    private static void Table(List<(Sample Sample, double Duration)> samples, StringBuilder md)
    {
        md.AppendLine("| эндпоинт | запросов | p50, мс | p95, мс | p99, мс | 2xx | 409 | 429 | 503 | 500 | прочие |")
            .AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var g in samples.GroupBy(x => x.Sample.Endpoint).OrderBy(x => x.Key))
        {
            var sorted = g.Select(x => x.Duration).Order().ToList();
            var list = g.ToList();
            var other = list.Count(x => x.Sample.Status is < 200 or (>= 300 and not 409 and not 429 and not 503 and not 500));
            md.AppendLine($"| {g.Key} | {list.Count} | {Stats.Percentile(sorted, 50):F1} | {Stats.Percentile(sorted, 95):F1} | {Stats.Percentile(sorted, 99):F1} | " +
                $"{list.Count(x => x.Sample.Status is >= 200 and < 300)} | {Share(list, 409)} | {Share(list, 429)} | {Share(list, 503)} | {Share(list, 500)} | {other} |");
        }

        md.AppendLine();
    }

    private static int Share(List<(Sample Sample, double Duration)> samples, int status)
    {
        return samples.Count(x => x.Sample.Status == status);
    }

    private static double P95(IEnumerable<(Sample Sample, double Duration)> samples)
    {
        var sorted = samples.Select(x => x.Duration).Order().ToList();
        return sorted.Count == 0 ? 0 : Stats.Percentile(sorted, 95);
    }
}
