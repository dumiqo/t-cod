using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Contract;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Idempotency;

[TestClass]
public class EnrollIdempotencyTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task Enroll_RepeatWithSameKeyAndBody_Returns201WithFirstBodyAndOneEnrollment(int seats)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        app.Clock.Advance(TimeSpan.FromTicks(1234567));
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":{seats}}}";

        using var first = await PostAsync(client, json, "key-1");
        var firstText = await first.Content.ReadAsStringAsync();
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        using var repeat = await PostAsync(client, json, "key-1");

        var repeatText = await repeat.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstText);
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, repeatText);
        Assert.AreEqual("application/json", repeat.Content.Headers.ContentType?.MediaType);
        Assert.IsNull(repeat.Headers.Location);
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        using var body = JsonDocument.Parse(repeatText);
        var root = body.RootElement;
        CollectionAssert.AreEqual(
            new[] { "classId", "createdAt", "id", "seats", "status" },
            root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.AreEqual(enrollment.Id, root.GetProperty("id").GetInt64());
        Assert.AreEqual(classId, root.GetProperty("classId").GetInt64());
        Assert.AreEqual(seats, root.GetProperty("seats").GetInt32());
        Assert.AreEqual("active", root.GetProperty("status").GetString());
        Assert.AreEqual(
            enrollment.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.ffffff") + "Z",
            root.GetProperty("createdAt").GetString());
        StringAssert.EndsWith(root.GetProperty("createdAt").GetString(), ".123456Z");
        Assert.AreEqual(firstText, repeatText);
        Assert.AreEqual(10 - seats, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_Created_StoresKeyRowWithHashAndResult()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        app.Clock.Advance(TimeSpan.FromTicks(1234567));
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":2}}", "key-1");

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        var row = await context.IdempotencyKeys.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id);
        Assert.IsNotNull(row);
        Assert.AreEqual(IdempotentOperation.Enroll, row.Operation);
        CollectionAssert.AreEqual(SHA256.HashData(Encoding.UTF8.GetBytes("key-1")), row.KeyHash);
        Assert.AreEqual(classId, row.ClassId);
        Assert.AreEqual(2, row.Seats);
        Assert.AreEqual(enrollment.Id, row.ResultId);
        Assert.AreEqual(enrollment.CreatedAt, row.ResultCreatedAt);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task Enroll_RepeatAtOrAfterClassStart_Returns201NotClassStarted(long ticksAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var startsAt = app.Clock.GetUtcNow().UtcDateTime.AddHours(1);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";

        using var first = await PostAsync(client, json, "key-1");
        var firstText = await first.Content.ReadAsStringAsync();
        app.Clock.Advance(startsAt - app.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromTicks(ticksAfterStart));
        using var repeat = await PostAsync(client, json, "key-1");

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstText);
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, await repeat.Content.ReadAsStringAsync());
        Assert.AreEqual(firstText, await repeat.Content.ReadAsStringAsync());
        await AssertSingleEnrollmentAsync(app, user, 9);
    }

    [TestMethod]
    public async Task Enroll_RepeatOnCancelledClass_Returns201NotClassCancelled()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";

        using var first = await PostAsync(client, json, "key-1");
        var firstText = await first.Content.ReadAsStringAsync();
        await using (var context = app.CreateContext())
        {
            await context.Classes.IgnoreQueryFilters()
                .Where(x => x.Id == classId)
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.IsCancelled, true));
        }

        using var repeat = await PostAsync(client, json, "key-1");

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstText);
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, await repeat.Content.ReadAsStringAsync());
        Assert.AreEqual(firstText, await repeat.Content.ReadAsStringAsync());
        await AssertSingleEnrollmentAsync(app, user, 9);
    }

    [TestMethod]
    public async Task Enroll_SameKeyOtherClass_Returns409IdempotencyKeyReusedWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        var otherId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var first = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", "key-1");
        var keyBefore = await KeyRowsAsync(app, user);
        using var second = await PostAsync(client, $"{{\"classId\":{otherId},\"seats\":1}}", "key-1");

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        await AssertConflictAsync(second, "idempotency-key-reused");
        await AssertSingleEnrollmentAsync(app, user, 9);
        await AssertNoEnrollmentsAsync(app, otherId);
        CollectionAssert.AreEqual(keyBefore, await KeyRowsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_Rejected409_LeavesNoKey()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(cancelled: true);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", "key-1");

        await AssertConflictAsync(response, "class-cancelled");
        CollectionAssert.AreEqual(Array.Empty<string>(), await KeyRowsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_ClassNotFound_LeavesNoKey()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, "{\"classId\":999999999,\"seats\":1}", "key-1");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        CollectionAssert.AreEqual(Array.Empty<string>(), await KeyRowsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_KeyAfter400_CreatesNewEnrollment()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var invalid = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":0}}", "key-1");
        await InputRulesTests.AssertValidationProblemAsync(invalid, "Seats");
        CollectionAssert.AreEqual(Array.Empty<string>(), await KeyRowsAsync(app, user));
        using var valid = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":2}}", "key-1");

        Assert.AreEqual(HttpStatusCode.Created, valid.StatusCode, await valid.Content.ReadAsStringAsync());
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(2, enrollment.Seats);
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_LongRandomKey_StoredByHashAndNotTruncated()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var key = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 4000);
        var almost = key[..^1] + (key[^1] == 'a' ? 'b' : 'a');
        var json = $"{{\"classId\":{classId},\"seats\":1}}";

        using var first = await PostAsync(client, json, key);
        using var repeat = await PostAsync(client, json, key);
        using var other = await PostAsync(client, json, almost);

        var firstText = await first.Content.ReadAsStringAsync();
        Assert.IsTrue(Encoding.UTF8.GetByteCount(key) > 2704);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstText);
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode);
        Assert.AreEqual(firstText, await repeat.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Created, other.StatusCode);
        await AssertEnrollmentCountAsync(app, user, 2, 8);
    }

    [TestMethod]
    public async Task Enroll_KeyInOtherCase_CreatesNewEnrollment()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";

        using var first = await PostAsync(client, json, "Key-A");
        using var second = await PostAsync(client, json, "key-a");

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        Assert.AreNotEqual(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        await AssertEnrollmentCountAsync(app, user, 2, 8);
    }

    [TestMethod]
    public async Task Enroll_NonAsciiKey_RepeatReturnsSameBody()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";

        using var first = await PostAsync(client, json, "ключ");
        using var repeat = await PostAsync(client, json, "ключ");

        var firstText = await first.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstText);
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode);
        Assert.AreEqual(firstText, await repeat.Content.ReadAsStringAsync());
        await AssertSingleEnrollmentAsync(app, user, 9);
        await using var context = app.CreateContext();
        var row = await context.IdempotencyKeys.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(
            "1de36a32af798da0c1ac9297603a320ed8fe567cf21c9177112a4ce914ebb8be",
            Convert.ToHexStringLower(row.KeyHash));
    }

    [TestMethod]
    public async Task Enroll_SameKeyOtherUser_CreatesHisOwnEnrollment()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        using var otherClient = await app.CreateClientAsync(other.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";

        using var first = await PostAsync(client, json, "key-1");
        using var second = await PostAsync(otherClient, json, "key-1");

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        await using var context = app.CreateContext();
        var otherEnrollment = await context.Enrollments.AsNoTracking().SingleAsync(x => x.UserId == other.Id);
        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.AreEqual(otherEnrollment.Id, body.RootElement.GetProperty("id").GetInt64());
        await AssertSingleEnrollmentAsync(app, user, 9);
        await AssertSingleEnrollmentAsync(app, other, 9);
    }

    private static async Task AssertSingleEnrollmentAsync(TestApp app, TestUser user, int remainingVisits)
    {
        await AssertEnrollmentCountAsync(app, user, 1, remainingVisits);
    }

    private static async Task AssertEnrollmentCountAsync(TestApp app, TestUser user, int count, int remainingVisits)
    {
        await using var context = app.CreateContext();

        Assert.AreEqual(count, await context.Enrollments.CountAsync(x => x.UserId == user.Id));
        Assert.AreEqual(remainingVisits, await RemainingVisitsAsync(app, user));
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
