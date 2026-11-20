using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Idempotency;
using WebApi.Redis;
using WebApi.Tests.Enrollments;
using WebApi.Tests.Infrastructure;
using WebApi.Tests.Waitlist;

namespace WebApi.Tests.Invariants;

public enum RedisMode
{
    Working,
    Broken,
    Hung
}

[TestClass]
public class MixedLoadInvariantTests
{
    [TestMethod]
    [DoNotParallelize]
    [DataRow(2903, RedisMode.Working)]
    [DataRow(2903, RedisMode.Broken)]
    [DataRow(2903, RedisMode.Hung)]
    [DataRow(1729, RedisMode.Working)]
    [DataRow(1729, RedisMode.Broken)]
    [DataRow(1729, RedisMode.Hung)]
    public async Task MixedWrites_TwoWaves_InvariantsHold(int seed, RedisMode mode)
    {
        await using var proxy = FreezableRedisProxy.Start(TestDatabase.RedisConnectionString);
        await using var app = await TestApp.StartAsync(builder =>
        {
            // решение: пул приложения меньше max_connections = 100 у Postgres: 200 одновременных запросов ждут
            // соединение в пуле, а не получают 53300 too many clients, когда в полном прогоне открыты чужие пулы.
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = configuration.Build()["ConnectionStrings:Postgres"] + ";Maximum Pool Size=40"
            }));

            if (mode == RedisMode.Hung)
            {
                builder
                    .UseSetting("Redis:OperationTimeout", "00:00:00.200")
                    .ConfigureTestServices(services => services.AddSingleton(_ => KeyPrefixedConnection.Wrap(
                        RedisConnection.Connect(proxy.ConnectionString),
                        $"test:{Guid.NewGuid():N}:")));
            }
        });
        var scenario = new MixedScenario(seed);
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);

        var users = new List<TestUser>();
        foreach (var (remaining, expired) in scenario.Passes)
        {
            users.Add(await app.Data.UserAsync(remaining, expired ? today.AddDays(-1) : null));
        }

        var classes = new List<long>();
        for (var c = 0; c < MixedScenario.Capacities.Length; c++)
        {
            classes.Add(await app.Data.ClassAsync(
                MixedScenario.Capacities[c],
                c == MixedScenario.ShortClass ? now.AddHours(1) : now.AddDays(1)));
        }

        var (enrollmentPlan, entryPlan) = scenario.Setup();
        var enrollments = new List<Placed>();
        foreach (var x in enrollmentPlan)
        {
            enrollments.Add(new Placed(x.Class, x.User, x.Seats, await app.Data.EnrollmentAsync(users[x.User].Id, classes[x.Class], x.Seats)));
        }

        var entries = new List<Placed>();
        foreach (var x in entryPlan)
        {
            entries.Add(new Placed(x.Class, x.User, x.Seats, await WaitlistApi.EntryAsync(app, users[x.User].Id, classes[x.Class], x.Seats)));
        }

        var clients = new List<HttpClient>();
        foreach (var user in users)
        {
            clients.Add(await app.CreateClientAsync(user.Email));
        }

        var userIds = users.Select(x => x.Id).ToList();
        var initial = await InvariantChecker.BalancesAsync(app.ConnectionString, classes, userIds);

        if (mode == RedisMode.Broken)
        {
            app.BreakRedis();
        }

        if (mode == RedisMode.Hung)
        {
            proxy.Freeze();
        }

        var firstOps = scenario.FirstWave(enrollments, entries, classes);
        var first = await Task.WhenAll(firstOps.Select(x => SendAsync(clients[x.User], x)));
        var secondOps = scenario.SecondWave(first, classes);
        var second = await Task.WhenAll(secondOps.Select(x => SendAsync(clients[x.User], x)));
        var outcomes = first.Concat(second).ToList();

        var violations = await InvariantChecker.CheckAsync(app.ConnectionString, classes, initial);
        violations.AddRange(CheckStatuses(outcomes));
        violations.AddRange(await CheckAgainstDbAsync(app, users, classes, enrollments, entries, outcomes, initial));
        violations.AddRange(await CheckCardsAsync(app, users, clients, classes));

        foreach (var client in clients)
        {
            client.Dispose();
        }

        var dump = $"seed {seed}, redis {mode}\n" + string.Join("\n", outcomes);
        if (violations.Count > 0)
        {
            Assert.Fail($"{string.Join("\n", violations)}\n---\n{dump}");
        }

        await AssertCoverageAsync(app, classes, outcomes, dump);
    }

    private static async Task<Outcome> SendAsync(HttpClient client, MixedOp op)
    {
        var body = $"{{\"classId\":{op.Target},\"seats\":{op.Seats}}}";
        using var response = op.Kind switch
        {
            OpKind.Enroll => await EnrollApi.PostAsync(client, body, op.Key),
            OpKind.Join => await WaitlistApi.PostAsync(client, body, op.Key),
            OpKind.Cancel => await CancelApi.CancelAsync(client, op.Target),
            OpKind.ChangeSeats => await ChangeSeatsApi.ChangeSeatsAsync(client, op.Target, op.Seats),
            OpKind.Leave => await client.PostAsync($"/enrollments/waitlist/{op.Target}/cancel", null),
            _ => throw new ArgumentOutOfRangeException(nameof(op))
        };
        var text = await response.Content.ReadAsStringAsync();
        string? reason = null;
        long? id = null;

        if (text.StartsWith('{'))
        {
            using var json = JsonDocument.Parse(text);
            reason = json.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            id = (int)response.StatusCode == 201 ? json.RootElement.GetProperty("id").GetInt64() : null;
        }

        return new Outcome(op, (int)response.StatusCode, reason, id, response.Headers.Contains("Retry-After"), text);
    }

    private static IEnumerable<string> CheckStatuses(List<Outcome> outcomes)
    {
        foreach (var x in outcomes)
        {
            if (x.Status is not (200 or 201 or 409 or 503))
            {
                yield return $"unexpected {x.Status}: {x} {x.Body}";
            }

            if (x.Status == 503 && !x.RetryAfter)
            {
                yield return $"503 without Retry-After: {x}";
            }
        }

        foreach (var key in outcomes.Where(x => x.Status == 201).GroupBy(x => (x.Op.User, x.Op.Kind, x.Op.Key)))
        {
            if (key.Select(x => x.Id).Distinct().Count() != 1)
            {
                yield return $"key {key.Key} returned different ids: {string.Join(", ", key.Select(x => x.Id))}";
            }
        }
    }

    private static async Task<List<string>> CheckAgainstDbAsync(
        TestApp app,
        List<TestUser> users,
        List<long> classes,
        List<Placed> enrollments,
        List<Placed> entries,
        List<Outcome> outcomes,
        Dictionary<long, int> initial)
    {
        var violations = new List<string>();
        await using var context = app.CreateContext();
        var userIds = users.Select(x => x.Id).ToList();
        var dbEnrollments = await context.Enrollments.AsNoTracking().Where(x => classes.Contains(x.ClassId)).ToDictionaryAsync(x => x.Id);
        var dbEntries = await context.WaitlistEntries.AsNoTracking().Where(x => classes.Contains(x.ClassId)).ToDictionaryAsync(x => x.Id);
        var created = Created(outcomes, OpKind.Enroll);
        var joined = Created(outcomes, OpKind.Join);

        foreach (var x in created.Concat(joined))
        {
            var (userId, classId) = x.Op.Kind == OpKind.Enroll
                ? dbEnrollments.TryGetValue(x.Id!.Value, out var e) ? (e.UserId, e.ClassId) : (0, 0)
                : dbEntries.TryGetValue(x.Id!.Value, out var w) ? (w.UserId, w.ClassId) : (0, 0);

            if (userId != users[x.Op.User].Id || classId != x.Op.Target)
            {
                violations.Add($"201 not in db as user {users[x.Op.User].Id} class {x.Op.Target}: {x}");
            }
        }

        var promoted = dbEntries.Values.Where(x => x.Status == WaitlistStatuses.Promoted).ToList();
        var expectedEnrollments = enrollments.Select(x => x.Id).Concat(created.Select(x => x.Id!.Value)).Concat(promoted.Select(x => x.Id)).Order();
        if (!expectedEnrollments.SequenceEqual(dbEnrollments.Keys.Order()))
        {
            violations.Add($"enrollments in db {string.Join(",", dbEnrollments.Keys.Order())}, expected {string.Join(",", expectedEnrollments)}");
        }

        var expectedEntries = entries.Select(x => x.Id).Concat(joined.Select(x => x.Id!.Value)).Order();
        if (!expectedEntries.SequenceEqual(dbEntries.Keys.Order()))
        {
            violations.Add($"waitlist in db {string.Join(",", dbEntries.Keys.Order())}, expected {string.Join(",", expectedEntries)}");
        }

        var dbKeys = await context.IdempotencyKeys.AsNoTracking().Where(x => userIds.Contains(x.UserId)).ToListAsync();
        var actualKeys = dbKeys.Select(x => $"{x.UserId}|{x.Operation}|{Convert.ToHexString(x.KeyHash)}|{x.ResultId}").Order();
        var expectedKeys = outcomes.Where(x => x.Status == 201)
            .Select(x => $"{users[x.Op.User].Id}|{(x.Op.Kind == OpKind.Enroll ? IdempotentOperation.Enroll : IdempotentOperation.JoinWaitlist)}|{Convert.ToHexString(IdempotencyKeyHash.Compute(x.Op.Key!))}|{x.Id}")
            .Distinct()
            .Order();
        if (!expectedKeys.SequenceEqual(actualKeys))
        {
            violations.Add($"idempotency keys in db:\n{string.Join("\n", actualKeys)}\nexpected:\n{string.Join("\n", expectedKeys)}");
        }

        foreach (var x in outcomes.Where(x => x.Status == 200 && x.Op.Kind is OpKind.Cancel or OpKind.Leave))
        {
            var status = x.Op.Kind == OpKind.Cancel ? dbEnrollments[x.Op.Target].Status : dbEntries[x.Op.Target].Status;
            if (status != "cancelled")
            {
                violations.Add($"200 but {status} in db: {x}");
            }
        }

        var cancelledEntries = dbEntries.Values.Where(x => x.Status == WaitlistStatuses.Cancelled).ToList();
        var remainingDrop = initial.Sum(x => x.Value) - enrollments.Sum(x => x.Seats)
            - await context.Passes.Where(x => userIds.Contains(x.UserId)).SumAsync(x => x.RemainingVisits);
        var events = app.Logs.GetSnapshot();
        int Events(int id) => events.Count(x => x.Id.Id == id);
        long Metric(string name) => EnrollApi.MetricSum(app, name);

        var expected = new (string Name, long Expected, long Actual)[]
        {
            ("club.enrollments.created", created.Sum(x => x.Op.Seats), Metric("club.enrollments.created")),
            ("event 2001", created.Count, Events(2001)),
            ("club.idempotency.replayed", outcomes.Count(x => x.Status == 201) - created.Count - joined.Count, Metric("club.idempotency.replayed")),
            ("event 1004", outcomes.Count(x => x.Status == 201) - created.Count - joined.Count, Events(1004)),
            ("club.waitlist.joined", joined.Sum(x => x.Op.Seats), Metric("club.waitlist.joined")),
            ("event 3001", joined.Count, Events(3001)),
            ("club.waitlist.left", cancelledEntries.Sum(x => x.Seats), Metric("club.waitlist.left")),
            ("event 3002", cancelledEntries.Count, Events(3002)),
            ("club.waitlist.promoted", promoted.Sum(x => x.Seats), Metric("club.waitlist.promoted")),
            ("event 3003", promoted.Count, Events(3003)),
            ("charged - refunded", remainingDrop, Metric("club.visits.charged") - Metric("club.visits.refunded")),
            ("club.requests.rejected", outcomes.Count(x => x.Status is 409 or 503), Metric("club.requests.rejected")),
            ("event 1001", outcomes.Count(x => x.Status == 409), Events(1001)),
            ("event 1002", outcomes.Count(x => x.Status == 503), Events(1002))
        };
        violations.AddRange(expected.Where(x => x.Expected != x.Actual).Select(x => $"{x.Name}: expected {x.Expected}, got {x.Actual}"));

        return violations;
    }

    private static List<Outcome> Created(List<Outcome> outcomes, OpKind kind)
    {
        return outcomes.Where(x => x.Status == 201 && x.Op.Kind == kind).DistinctBy(x => x.Id).ToList();
    }

    private static async Task<List<string>> CheckCardsAsync(TestApp app, List<TestUser> users, List<HttpClient> clients, List<long> classes)
    {
        var violations = new List<string>();
        await using var context = app.CreateContext();
        var rows = await context.Enrollments.AsNoTracking()
            .Where(x => classes.Contains(x.ClassId))
            .Join(context.Passes, e => e.UserId, p => p.UserId, (e, p) => new { e.Id, e.UserId, e.Status, e.Seats, p.RemainingVisits })
            .ToListAsync();

        foreach (var row in rows)
        {
            var client = clients[users.FindIndex(x => x.Id == row.UserId)];
            var card = await client.GetFromJsonAsync<JsonElement>($"/enrollments/{row.Id}");
            var actual = $"{card.GetProperty("status").GetString()}|{card.GetProperty("seats").GetInt32()}|{card.GetProperty("pass").GetProperty("remainingVisits").GetInt32()}";
            var expected = $"{row.Status}|{row.Seats}|{row.RemainingVisits}";

            if (actual != expected)
            {
                violations.Add($"card {row.Id}: {actual}, db {expected}");
            }
        }

        return violations;
    }

    private static async Task AssertCoverageAsync(TestApp app, List<long> classes, List<Outcome> outcomes, string dump)
    {
        await using var context = app.CreateContext();
        var promotions = await context.WaitlistEntries.CountAsync(x => classes.Contains(x.ClassId) && x.Status == WaitlistStatuses.Promoted);
        var created = outcomes.Where(x => x.Status == 201).GroupBy(x => (x.Op.Kind, x.Id)).ToList();
        var missing = new (string Name, bool Covered)[]
        {
            ("new enrollment", created.Any(x => x.Key.Kind == OpKind.Enroll)),
            ("new waitlist entry", created.Any(x => x.Key.Kind == OpKind.Join)),
            ("replay", created.Any(x => x.Count() > 1)),
            ("seats changed", outcomes.Any(x => x.Status == 200 && x.Op.Kind == OpKind.ChangeSeats)),
            ("cancelled", outcomes.Any(x => x.Status == 200 && x.Op.Kind == OpKind.Cancel)),
            ("left", outcomes.Any(x => x.Status == 200 && x.Op.Kind == OpKind.Leave)),
            ("capacity-exceeded", outcomes.Any(x => x.Reason == "capacity-exceeded")),
            ("already-waiting", outcomes.Any(x => x.Reason == "already-waiting")),
            ("promotion", promotions > 0)
        }.Where(x => !x.Covered).Select(x => x.Name).ToList();

        if (missing.Count > 0)
        {
            Assert.Fail($"scenario too weak, not covered: {string.Join(", ", missing)}\n---\n{dump}");
        }
    }
}
