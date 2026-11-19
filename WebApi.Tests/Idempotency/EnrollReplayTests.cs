using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Idempotency;

[TestClass]
public class EnrollReplayTests
{
    [TestMethod]
    [DataRow("class-started", true)]
    [DataRow("class-started", false)]
    [DataRow("class-cancelled", true)]
    [DataRow("class-cancelled", false)]
    [DataRow("pass-expired", true)]
    [DataRow("pass-expired", false)]
    [DataRow("pass-exhausted", true)]
    [DataRow("pass-exhausted", false)]
    [DataRow("capacity-exceeded", true)]
    [DataRow("capacity-exceeded", false)]
    public async Task Enroll_SameKeyOtherBodyThatWouldBeRejected_Returns409IdempotencyKeyReusedWithoutTrace(
        string reason,
        bool otherClass)
    {
        await using var app = await TestApp.StartAsync();
        var now = Now(app);
        var today = DateOnly.FromDateTime(now);
        var user = await app.Data.UserAsync(remainingVisits: 10, validUntil: reason == "pass-expired" ? today : null);
        var neighbour = await app.Data.UserAsync(remainingVisits: 10);
        var startsAt = now.AddHours(reason == "class-started" ? 1 : 240);
        var capacity = reason == "capacity-exceeded" ? 4 : 20;
        var classId = await app.Data.ClassAsync(capacity: capacity, startsAt: startsAt);
        var targetId = otherClass ? await app.Data.ClassAsync(capacity: capacity, startsAt: startsAt) : classId;
        var seats = reason switch
        {
            "pass-exhausted" => 9,
            "capacity-exceeded" => 3,
            _ => otherClass ? 2 : 1
        };
        using var client = await app.CreateClientAsync(user.Email);
        var originalJson = $"{{\"classId\":{classId},\"seats\":2}}";
        var otherJson = $"{{\"classId\":{targetId},\"seats\":{seats}}}";

        using var first = await PostAsync(client, originalJson, "key-1");
        var firstText = await first.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstText);
        switch (reason)
        {
            case "class-started":
                app.Clock.SetUtcNow(startsAt);
                break;
            case "class-cancelled":
                await CancelClassAsync(app, targetId);
                break;
            case "pass-expired":
                app.Clock.SetUtcNow(today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
                break;
            case "capacity-exceeded" when otherClass:
                await app.Data.EnrollmentAsync(neighbour.Id, targetId, 2);
                break;
        }

        var enrollmentsBefore = await UserEnrollmentsAsync(app, user);
        var keyBefore = await KeyRowsAsync(app, user);
        using var reused = await PostAsync(client, otherJson, "key-1");
        using var control = await PostAsync(client, otherJson, "key-2");
        using var repeat = await PostAsync(client, originalJson, "key-1");

        await AssertConflictAsync(reused, "idempotency-key-reused");
        await AssertConflictAsync(control, reason);
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, await repeat.Content.ReadAsStringAsync());
        Assert.AreEqual(firstText, await repeat.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(enrollmentsBefore, await UserEnrollmentsAsync(app, user));
        CollectionAssert.AreEqual(keyBefore, await KeyRowsAsync(app, user));
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_SameKeyOtherBodyWithUnknownClass_Returns404WithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var first = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":2}}", "key-1");
        var enrollmentsBefore = await UserEnrollmentsAsync(app, user);
        var keyBefore = await KeyRowsAsync(app, user);
        using var second = await PostAsync(client, "{\"classId\":999999999,\"seats\":2}", "key-1");

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, second.StatusCode, await second.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(enrollmentsBefore, await UserEnrollmentsAsync(app, user));
        CollectionAssert.AreEqual(keyBefore, await KeyRowsAsync(app, user));
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow("{\"seats\":2,\"classId\":CLASS}")]
    [DataRow("  {\n  \"classId\" :  CLASS ,\r\n\t\"seats\":2 }  ")]
    [DataRow("{\"classId\":CLASS,\"seats\":2,\"x\":1}")]
    [DataRow("{\"x\":{\"seats\":5},\"classId\":CLASS,\"seats\":2}")]
    public async Task Enroll_RepeatWithSameValuesInOtherJson_ReturnsFirstBody(string repeatJson)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var first = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":2}}", "key-1");
        var firstText = await first.Content.ReadAsStringAsync();
        var keyBefore = await KeyRowsAsync(app, user);
        using var repeat = await PostAsync(client, repeatJson.Replace("CLASS", classId.ToString()), "key-1");

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstText);
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, await repeat.Content.ReadAsStringAsync());
        Assert.AreEqual(firstText, await repeat.Content.ReadAsStringAsync());
        Assert.AreEqual(1, (await UserEnrollmentsAsync(app, user)).Length);
        CollectionAssert.AreEqual(keyBefore, await KeyRowsAsync(app, user));
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow("class-cancelled")]
    [DataRow("pass-expired")]
    [DataRow("pass-exhausted")]
    [DataRow("capacity-exceeded")]
    public async Task Enroll_RepeatAfter409WhenReasonIsGone_Returns201AndNewEnrollment(string reason)
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(Now(app));
        var user = await app.Data.UserAsync(
            remainingVisits: reason == "pass-exhausted" ? 1 : 10,
            validUntil: reason == "pass-expired" ? today.AddDays(-1) : null);
        var neighbour = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(capacity: 3, cancelled: reason == "class-cancelled");
        var occupiedId = reason == "capacity-exceeded" ? await app.Data.EnrollmentAsync(neighbour.Id, classId, 2) : 0;
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":2}}";

        using var rejected = await PostAsync(client, json, "key-1");
        await AssertConflictAsync(rejected, reason);
        await using (var context = app.CreateContext())
        {
            switch (reason)
            {
                case "class-cancelled":
                    await context.Classes.IgnoreQueryFilters()
                        .Where(x => x.Id == classId)
                        .ExecuteUpdateAsync(x => x.SetProperty(c => c.IsCancelled, false));
                    break;
                case "pass-expired":
                    await context.Passes.Where(x => x.UserId == user.Id)
                        .ExecuteUpdateAsync(x => x.SetProperty(p => p.ValidUntil, today));
                    break;
                case "pass-exhausted":
                    await context.Passes.Where(x => x.UserId == user.Id)
                        .ExecuteUpdateAsync(x => x.SetProperty(p => p.RemainingVisits, 10));
                    break;
                case "capacity-exceeded":
                    await context.Enrollments.Where(x => x.Id == occupiedId)
                        .ExecuteDeleteAsync();
                    break;
            }
        }

        using var repeat = await PostAsync(client, json, "key-1");

        var text = await repeat.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, text);
        await using var check = app.CreateContext();
        var enrollment = await check.Enrollments.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(classId, enrollment.ClassId);
        Assert.AreEqual(2, enrollment.Seats);
        Assert.AreEqual(EnrollmentStatuses.Active, enrollment.Status);
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual(enrollment.Id, body.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
        var row = await check.IdempotencyKeys.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(enrollment.Id, row.ResultId);
    }

    [TestMethod]
    public async Task Enroll_RepeatAfter404WhenClassAppeared_Returns201AndNewEnrollment()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        using var client = await app.CreateClientAsync(user.Email);
        const long classId = 900_000_000_000;
        var json = $"{{\"classId\":{classId},\"seats\":2}}";

        using var notFound = await PostAsync(client, json, "key-1");
        Assert.AreEqual(HttpStatusCode.NotFound, notFound.StatusCode, await notFound.Content.ReadAsStringAsync());
        await using (var context = app.CreateContext())
        {
            var roomId = await context.Rooms.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
            context.Classes.Add(new Class
            {
                Id = classId,
                RoomId = roomId,
                Title = "Late class",
                StartsAt = Now(app).AddDays(1),
                DurationMinutes = 60,
                Capacity = 10
            });
            await context.SaveChangesAsync();
        }

        using var repeat = await PostAsync(client, json, "key-1");

        var text = await repeat.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, text);
        await using var check = app.CreateContext();
        var enrollment = await check.Enrollments.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(classId, enrollment.ClassId);
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual(enrollment.Id, body.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(classId, body.RootElement.GetProperty("classId").GetInt64());
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_RepeatAfterEnrollmentCancelled_ReturnsFirstSnapshotAndKeepsEnrollment()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":3}}";

        using var first = await PostAsync(client, json, "key-1");
        var firstText = await first.Content.ReadAsStringAsync();
        var cancelledAt = Now(app).AddMinutes(1);
        await using (var context = app.CreateContext())
        {
            await context.Enrollments.Where(x => x.UserId == user.Id)
                .ExecuteUpdateAsync(x => x
                    .SetProperty(e => e.Status, "cancelled")
                    .SetProperty(e => e.CancelledAt, cancelledAt)
                    .SetProperty(e => e.RefundedVisits, 3));
        }

        var enrollmentsBefore = await UserEnrollmentsAsync(app, user);
        using var repeat = await PostAsync(client, json, "key-1");

        var text = await repeat.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstText);
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, text);
        Assert.AreEqual(firstText, text);
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual("active", body.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(3, body.RootElement.GetProperty("seats").GetInt32());
        CollectionAssert.AreEqual(enrollmentsBefore, await UserEnrollmentsAsync(app, user));
        StringAssert.Contains(enrollmentsBefore.Single(), "|cancelled|");
        Assert.AreEqual(7, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_RepeatAfterSeatsChanged_ReturnsFirstSnapshotAndKeepsEnrollment()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":3}}";

        using var first = await PostAsync(client, json, "key-1");
        var firstText = await first.Content.ReadAsStringAsync();
        await using (var context = app.CreateContext())
        {
            await context.Enrollments.Where(x => x.UserId == user.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(e => e.Seats, 1));
        }

        var enrollmentsBefore = await UserEnrollmentsAsync(app, user);
        using var repeat = await PostAsync(client, json, "key-1");

        var text = await repeat.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstText);
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, text);
        Assert.AreEqual(firstText, text);
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual(3, body.RootElement.GetProperty("seats").GetInt32());
        CollectionAssert.AreEqual(enrollmentsBefore, await UserEnrollmentsAsync(app, user));
        StringAssert.Contains(enrollmentsBefore.Single(), $"|{classId}|1|active|");
        Assert.AreEqual(7, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_TwoKeyHeaders_KeyIsJoinedWithComma()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";
        using var twoHeaders = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        twoHeaders.Headers.TryAddWithoutValidation("Idempotency-Key", new[] { "a", "b" });

        using var first = await client.SendAsync(twoHeaders);
        var firstText = await first.Content.ReadAsStringAsync();
        using var joined = await PostAsync(client, json, "a,b");
        using var single = await PostAsync(client, json, "a");

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstText);
        Assert.AreEqual(HttpStatusCode.Created, joined.StatusCode, await joined.Content.ReadAsStringAsync());
        Assert.AreEqual(firstText, await joined.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Created, single.StatusCode, await single.Content.ReadAsStringAsync());
        Assert.AreNotEqual(firstText, await single.Content.ReadAsStringAsync());
        await using var context = app.CreateContext();
        var hashes = await context.IdempotencyKeys.AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .OrderBy(x => x.Id)
            .Select(x => x.KeyHash)
            .ToListAsync();
        CollectionAssert.AreEqual(
            new[] { Hex("a,b"), Hex("a") },
            hashes.Select(Convert.ToHexStringLower).ToArray());
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }

    private static string Hex(string key)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private static async Task CancelClassAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();
        await context.Classes.IgnoreQueryFilters()
            .Where(x => x.Id == classId)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.IsCancelled, true));
    }

    private static async Task<string[]> UserEnrollmentsAsync(TestApp app, TestUser user)
    {
        await using var context = app.CreateContext();
        var enrollments = await context.Enrollments.AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .OrderBy(x => x.Id)
            .ToListAsync();

        return enrollments.Select(Snapshot).ToArray();
    }

    private static async Task<string[]> KeyRowsAsync(TestApp app, TestUser user)
    {
        await using var context = app.CreateContext();
        var rows = await context.IdempotencyKeys.AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .OrderBy(x => x.Id)
            .ToListAsync();

        return rows
            .Select(x => $"{x.Id}|{x.Operation}|{Convert.ToHexString(x.KeyHash)}|{x.ClassId}|{x.Seats}|{x.ResultId}|{x.ResultCreatedAt:O}")
            .ToArray();
    }
}
