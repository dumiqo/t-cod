using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Contract;
using WebApi.Tests.Enrollments;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class JoinWaitlistTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task Join_FullClass_Returns201WithEntry(int seats)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        var enrollmentsBefore = await EnrollApi.EnrollmentsSnapshotAsync(app, classId);
        var now = app.Clock.GetUtcNow().UtcDateTime;
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":{seats}}}");

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, text);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.IsNull(response.Headers.Location);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        CollectionAssert.AreEqual(
            new[] { "classId", "createdAt", "id", "position", "seats" },
            root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        await using var context = app.CreateContext();
        var entry = await context.WaitlistEntries.SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(entry.Id, root.GetProperty("id").GetInt64());
        Assert.AreEqual(classId, root.GetProperty("classId").GetInt64());
        Assert.AreEqual(seats, root.GetProperty("seats").GetInt32());
        Assert.AreEqual(1, root.GetProperty("position").GetInt32());
        Assert.AreEqual(now.ToString("yyyy-MM-ddTHH:mm:ssZ"), root.GetProperty("createdAt").GetString());
        Assert.AreEqual(classId, entry.ClassId);
        Assert.AreEqual(seats, entry.Seats);
        Assert.AreEqual("waiting", entry.Status);
        Assert.AreEqual(now, entry.CreatedAt);
        Assert.IsNull(entry.CancelledAt);
        CollectionAssert.AreEqual(enrollmentsBefore, await EnrollApi.EnrollmentsSnapshotAsync(app, classId));
        Assert.IsFalse(await context.Enrollments.AnyAsync(x => x.UserId == user.Id));
        Assert.AreEqual(10, await EnrollApi.RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Join_ThenEnrollElsewhere_IdSharedWithEnrollmentsSequence()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var fullId = await FullClassAsync(app);
        var freeId = await app.Data.ClassAsync(capacity: 10);
        using var client = await app.CreateClientAsync(user.Email);

        using var joined = await JoinAsync(client, fullId, 1);
        using var enrolled = await EnrollApi.EnrollAsync(client, freeId, 1);

        Assert.AreEqual(HttpStatusCode.Created, joined.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, enrolled.StatusCode);
        using var joinedBody = JsonDocument.Parse(await joined.Content.ReadAsStringAsync());
        using var enrolledBody = JsonDocument.Parse(await enrolled.Content.ReadAsStringAsync());
        var waitlistId = joinedBody.RootElement.GetProperty("id").GetInt64();
        var enrollmentId = enrolledBody.RootElement.GetProperty("id").GetInt64();
        await using var context = app.CreateContext();
        Assert.IsFalse(await context.Enrollments.AnyAsync(x => x.Id == waitlistId));
        Assert.AreEqual(waitlistId + 1, enrollmentId);
    }

    [TestMethod]
    public async Task Join_ThreeClients_PositionsOneTwoThree()
    {
        await using var app = await TestApp.StartAsync();
        var classId = await FullClassAsync(app);
        var positions = new List<int>();

        for (var i = 0; i < 3; i++)
        {
            var user = await app.Data.UserAsync();
            using var client = await app.CreateClientAsync(user.Email);
            using var response = await JoinAsync(client, classId, 1);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            positions.Add(body.RootElement.GetProperty("position").GetInt32());
        }

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, positions);
        await using var context = app.CreateContext();
        Assert.AreEqual(3, await context.WaitlistEntries.CountAsync(x => x.ClassId == classId && x.Status == "waiting"));
    }

    [TestMethod]
    public async Task Join_CancelledAndPromotedAheadAndOtherClass_CountsOnlyWaitingOfThisClass()
    {
        await using var app = await TestApp.StartAsync();
        var classId = await FullClassAsync(app);
        var otherId = await FullClassAsync(app);
        var statuses = new[] { (classId, "cancelled"), (classId, "waiting"), (classId, "promoted"), (otherId, "waiting") };

        foreach (var (id, status) in statuses)
        {
            var other = await app.Data.UserAsync();
            using var otherClient = await app.CreateClientAsync(other.Email);
            using var joined = await JoinAsync(otherClient, id, 1);
            Assert.AreEqual(HttpStatusCode.Created, joined.StatusCode);
            await using var update = app.CreateContext();
            await update.WaitlistEntries.Where(x => x.UserId == other.Id).ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, status));
        }

        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(2, body.RootElement.GetProperty("position").GetInt32());
    }

    [TestMethod]
    public async Task Join_ClockWithSubMicrosecondTicks_CreatedAtTruncatedToMicroseconds()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var start = app.Clock.GetUtcNow().UtcDateTime;
        app.Clock.Advance(TimeSpan.FromTicks(1234567));
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(
            start.ToString("yyyy-MM-ddTHH:mm:ss") + ".123456Z",
            body.RootElement.GetProperty("createdAt").GetString());
        await using var context = app.CreateContext();
        var entry = await context.WaitlistEntries.SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(start.AddTicks(1234560), entry.CreatedAt);
    }

    [TestMethod]
    [DataRow("{\"classId\":CLASS,\"seats\":0}", "Seats")]
    [DataRow("{\"classId\":CLASS,\"seats\":-1}", "Seats")]
    [DataRow("{\"classId\":CLASS,\"seats\":-2147483648}", "Seats")]
    [DataRow("{\"seats\":1}", "ClassId")]
    [DataRow("{\"classId\":CLASS}", "Seats")]
    [DataRow("{}", "ClassId", "Seats")]
    [DataRow("{\"classId\":null,\"seats\":1}", "ClassId")]
    [DataRow("{\"classId\":CLASS,\"seats\":null}", "Seats")]
    [DataRow("{\"classId\":\"abc\",\"seats\":1}", "$.classId", "request")]
    [DataRow("{\"classId\":\"CLASS\",\"seats\":1}", "$.classId", "request")]
    [DataRow("{\"classId\":CLASS,\"seats\":\"2\"}", "$.seats", "request")]
    [DataRow("{\"classId\":CLASS,\"seats\":1.5}", "$.seats", "request")]
    [DataRow("{\"classId\":true,\"seats\":1}", "$.classId", "request")]
    [DataRow("{\"classId\":1.5,\"seats\":1}", "$.classId", "request")]
    [DataRow("{\"classId\":9223372036854775808,\"seats\":1}", "$.classId", "request")]
    [DataRow("{\"classId\":CLASS,\"seats\":2147483648}", "$.seats", "request")]
    [DataRow("{\"classId\":CLASS,\"seats\":", "$.seats", "request")]
    [DataRow("{", "$", "request")]
    [DataRow("not json", "$", "request")]
    [DataRow("[]", "$", "request")]
    [DataRow("", "")]
    [DataRow("null", "")]
    public async Task Join_InvalidBody_Returns400WithoutTrace(string json, params string[] errorKeys)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, json.Replace("CLASS", classId.ToString()));

        await InputRulesTests.AssertValidationProblemAsync(response, errorKeys);
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Join_NoBodyNoContentType_Returns400WithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        using var client = await app.CreateClientAsync(user.Email);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist");
        request.Headers.Add("Idempotency-Key", "key");

        using var response = await client.SendAsync(request);

        await InputRulesTests.AssertValidationProblemAsync(response, string.Empty);
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    public async Task Join_MissingOrBlankIdempotencyKey_Returns400WithoutTrace(string? key)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", key);

        await InputRulesTests.AssertValidationProblemAsync(response, "Idempotency-Key");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Join_MissingKeyAndInvalidSeats_Returns400WithBothErrors()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":0}}", null);

        await InputRulesTests.AssertValidationProblemAsync(response, "Idempotency-Key", "Seats");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Join_MissingClassWithInvalidSeats_Returns400Not404()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, "{\"classId\":999999999,\"seats\":0}");

        await InputRulesTests.AssertValidationProblemAsync(response, "Seats");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Join_NoToken_Returns401WithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var classId = await FullClassAsync(app);
        using var client = app.CreateClient();

        using var response = await JoinAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var context = app.CreateContext();
        Assert.IsFalse(await context.WaitlistEntries.AnyAsync(x => x.ClassId == classId));
    }

    [TestMethod]
    [DataRow(999999999L)]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public async Task Join_ClassNotFound_Returns404WithoutTrace(long classId)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 1);

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, text);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        CollectionAssert.AreEqual(
            new[] { "status", "title", "traceId", "type" },
            root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.AreEqual(404, root.GetProperty("status").GetInt32());
        Assert.AreEqual("https://tools.ietf.org/html/rfc9110#section-15.5.5", root.GetProperty("type").GetString());
        StringAssert.Matches(root.GetProperty("traceId").GetString(), new Regex(EnrollApi.TraceParent));
        await AssertNoTraceAsync(app, user, 10);
    }
}
