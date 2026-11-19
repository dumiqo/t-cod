using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Contract;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class EnrollmentCardTests
{
    [TestMethod]
    public async Task GetCard_ActiveEnrollment_Returns200WithExactCard()
    {
        await using var app = await TestApp.StartAsync();
        var start = app.Clock.GetUtcNow().UtcDateTime;
        var user = await app.Data.UserAsync(remainingVisits: 7, validUntil: new DateOnly(2026, 9, 22));
        var startsAt = start.AddDays(1).AddTicks(6543210);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        app.Clock.Advance(TimeSpan.FromTicks(1234560));
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.GetAsync($"/enrollments/{id}");

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        await using var context = app.CreateContext();
        var room = await context.Classes.Where(x => x.Id == classId).Select(x => x.Room).SingleAsync();
        var expected = JsonSerializer.Serialize(new
        {
            id,
            status = "active",
            seats = 3,
            createdAt = start.ToString("yyyy-MM-ddTHH:mm:ss") + ".123456Z",
            @class = new
            {
                id = classId,
                title = "Test class",
                startsAt = start.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss") + ".654321Z",
                durationMinutes = 60,
                isCancelled = false,
                room = new { id = room.Id, name = room.Name }
            },
            pass = new { remainingVisits = 7, validUntil = "2026-09-22" }
        });
        AssertJson(expected, text);
        var enrollment = await context.Enrollments.SingleAsync(x => x.Id == id);
        Assert.AreEqual(start.AddTicks(1234560), enrollment.CreatedAt);
        Assert.AreEqual(startsAt, await context.Classes.Where(x => x.Id == classId).Select(x => x.StartsAt).SingleAsync());
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task GetCard_CancelledEnrollmentOrClass_Returns200WithStatusAndSeats(bool enrollmentCancelled, bool classCancelled)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 6);
        var classId = await app.Data.ClassAsync(cancelled: classCancelled);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 4, cancelled: enrollmentCancelled);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.GetAsync($"/enrollments/{id}");

        var root = await AssertOkAsync(response);
        Assert.AreEqual(id, root.GetProperty("id").GetInt64());
        Assert.AreEqual(enrollmentCancelled ? "cancelled" : "active", root.GetProperty("status").GetString());
        Assert.AreEqual(4, root.GetProperty("seats").GetInt32());
        Assert.AreEqual(classId, root.GetProperty("class").GetProperty("id").GetInt64());
        Assert.AreEqual(classCancelled, root.GetProperty("class").GetProperty("isCancelled").GetBoolean());
        Assert.AreEqual(6, root.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
    }

    [TestMethod]
    public async Task GetCard_NoToken_Returns401()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = app.CreateClient();

        using var response = await client.GetAsync($"/enrollments/{id}");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task GetCard_InvalidToken_Returns401()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        using var response = await client.GetAsync($"/enrollments/{id}");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("1.5")]
    [DataRow("9223372036854775808")]
    public async Task GetCard_IdNotLong_Returns400WithIdError(string id)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.GetAsync($"/enrollments/{id}");

        await InputRulesTests.AssertValidationProblemAsync(response, "id");
    }

    [TestMethod]
    [DataRow(999999999L)]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public async Task GetCard_UnknownId_Returns404(long id)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.GetAsync($"/enrollments/{id}");

        await AssertNotFoundAsync(response);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetCard_OtherUsersEnrollment_Returns404(bool otherCancelled)
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 1, cancelled: otherCancelled);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.GetAsync($"/enrollments/{id}");

        await AssertNotFoundAsync(response);
    }

    [TestMethod]
    public async Task GetCard_AdminOnOtherUsersEnrollment_Returns404()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await client.GetAsync($"/enrollments/{id}");

        await AssertNotFoundAsync(response);
    }

    [TestMethod]
    public async Task GetCard_MissingAndOthers_SameBodyExceptTraceId()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var others = await client.GetAsync($"/enrollments/{id}");
        using var missing = await client.GetAsync($"/enrollments/{id + 1000000}");

        Assert.AreEqual(HttpStatusCode.NotFound, others.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.AreEqual(missing.Content.Headers.ContentType?.ToString(), others.Content.Headers.ContentType?.ToString());
        var othersBody = JsonNode.Parse(await others.Content.ReadAsStringAsync())!.AsObject();
        var missingBody = JsonNode.Parse(await missing.Content.ReadAsStringAsync())!.AsObject();
        othersBody.Remove("traceId");
        missingBody.Remove("traceId");
        Assert.IsTrue(JsonNode.DeepEquals(missingBody, othersBody), $"{missingBody} vs {othersBody}");
    }

    [TestMethod]
    public async Task GetCard_FoundAndNotFound_WritesNoEventsOrMetrics()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        var own = await app.Data.EnrollmentAsync(user.Id, classId, 1);
        var others = await app.Data.EnrollmentAsync(owner.Id, classId, 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var found = await client.GetAsync($"/enrollments/{own}");
        using var notFound = await client.GetAsync($"/enrollments/{others}");

        Assert.AreEqual(HttpStatusCode.OK, found.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.IsFalse(app.Logs.GetSnapshot().Any(x => x.Id.Id is 1001 or 1002 or 1004 or 2001));
        foreach (var instrument in new[] { "club.requests.rejected", "club.enrollments.created", "club.visits.charged", "club.idempotency.replayed" })
        {
            Assert.AreEqual(0, app.Metrics(instrument).GetMeasurementSnapshot().Count, instrument);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task GetCard_ClassAlreadyStarted_Returns200(int microsecondsAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(startsAt: now.AddDays(1));
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        app.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromTicks(microsecondsAfterStart * 10));
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.GetAsync($"/enrollments/{id}");

        var root = await AssertOkAsync(response);
        Assert.AreEqual(
            now.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            root.GetProperty("class").GetProperty("startsAt").GetString());
        Assert.AreEqual("active", root.GetProperty("status").GetString());
    }

    [TestMethod]
    [DataRow(-1, 5)]
    [DataRow(0, 5)]
    [DataRow(30, 0)]
    public async Task GetCard_PassExpiredEndingTodayOrEmpty_Returns200WithExactPass(int validDays, int remainingVisits)
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime);
        var validUntil = today.AddDays(validDays);
        var user = await app.Data.UserAsync(remainingVisits: remainingVisits, validUntil: validUntil);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.GetAsync($"/enrollments/{id}");

        var root = await AssertOkAsync(response);
        var pass = root.GetProperty("pass");
        Assert.AreEqual(remainingVisits, pass.GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual(validUntil.ToString("yyyy-MM-dd"), pass.GetProperty("validUntil").GetString());
    }

    [TestMethod]
    public async Task GetCard_MaxSeats_Returns200WithSeats()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(capacity: int.MaxValue), int.MaxValue);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.GetAsync($"/enrollments/{id}");

        var root = await AssertOkAsync(response);
        Assert.AreEqual(int.MaxValue, root.GetProperty("seats").GetInt32());
    }

    [TestMethod]
    public async Task GetCard_AfterSecondEnrollment_ShowsCurrentPass()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        using var first = await EnrollAsync(client, classId, 2);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        var id = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        using var second = await EnrollAsync(client, classId, 3);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);

        using var response = await client.GetAsync($"/enrollments/{id}");

        var root = await AssertOkAsync(response);
        Assert.AreEqual(2, root.GetProperty("seats").GetInt32());
        Assert.AreEqual(5, root.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual(5, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task GetCard_TwoEnrollmentsOnSameClass_EachReturnsItsOwn()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        var first = await app.Data.EnrollmentAsync(user.Id, classId, 1);
        var second = await app.Data.EnrollmentAsync(user.Id, classId, 3, cancelled: true);
        using var client = await app.CreateClientAsync(user.Email);

        using var firstResponse = await client.GetAsync($"/enrollments/{first}");
        using var secondResponse = await client.GetAsync($"/enrollments/{second}");

        var firstRoot = await AssertOkAsync(firstResponse);
        var secondRoot = await AssertOkAsync(secondResponse);
        Assert.AreEqual(first, firstRoot.GetProperty("id").GetInt64());
        Assert.AreEqual(1, firstRoot.GetProperty("seats").GetInt32());
        Assert.AreEqual("active", firstRoot.GetProperty("status").GetString());
        Assert.AreEqual(second, secondRoot.GetProperty("id").GetInt64());
        Assert.AreEqual(3, secondRoot.GetProperty("seats").GetInt32());
        Assert.AreEqual("cancelled", secondRoot.GetProperty("status").GetString());
    }

    private static void AssertJson(string expected, string actual)
    {
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), $"expected {expected}, got {actual}");
    }

    private static async Task<JsonElement> AssertOkAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        using var body = JsonDocument.Parse(text);

        return body.RootElement.Clone();
    }

    private static async Task AssertNotFoundAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, text);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        CollectionAssert.AreEqual(
            new[] { "status", "title", "traceId", "type" },
            root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.AreEqual(404, root.GetProperty("status").GetInt32());
        Assert.AreEqual("Not Found", root.GetProperty("title").GetString());
        Assert.AreEqual("https://tools.ietf.org/html/rfc9110#section-15.5.5", root.GetProperty("type").GetString());
        StringAssert.Matches(root.GetProperty("traceId").GetString(), new Regex(TraceParent));
    }
}
