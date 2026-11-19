using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WebApi.Auth;
using WebApi.Tests.Contract;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class EnrollTests
{
    private const string TraceParent = "^00-[0-9a-f]{32}-[0-9a-f]{16}-00$";

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task Enroll_Valid_Returns201WithEnrollment(int seats)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(capacity: 10);
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
            new[] { "classId", "createdAt", "id", "seats", "status" },
            root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.SingleOrDefaultAsync(x => x.UserId == user.Id);
        Assert.IsNotNull(enrollment);
        Assert.AreEqual(enrollment.Id, root.GetProperty("id").GetInt64());
        Assert.AreEqual(classId, root.GetProperty("classId").GetInt64());
        Assert.AreEqual(seats, root.GetProperty("seats").GetInt32());
        Assert.AreEqual("active", root.GetProperty("status").GetString());
        Assert.AreEqual(now.ToString("yyyy-MM-ddTHH:mm:ssZ"), root.GetProperty("createdAt").GetString());
        Assert.AreEqual(classId, enrollment.ClassId);
        Assert.AreEqual(seats, enrollment.Seats);
        Assert.AreEqual("active", enrollment.Status);
        Assert.AreEqual(now, enrollment.CreatedAt);
        Assert.IsNull(enrollment.CancelledAt);
        Assert.AreEqual(10 - seats, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_Valid_LeavesOtherPassesAndEnrollmentsUntouched()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var other = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await using var before = app.CreateContext();
        var enrollmentsBefore = await before.Enrollments.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var passesBefore = await before.Passes.AsNoTracking()
            .Where(x => x.UserId != user.Id)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.RemainingVisits, x.ValidUntil })
            .ToListAsync();

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":2}}");

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        await using var after = app.CreateContext();
        var enrollmentsAfter = await after.Enrollments.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var passesAfter = await after.Passes.AsNoTracking()
            .Where(x => x.UserId != user.Id)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.RemainingVisits, x.ValidUntil })
            .ToListAsync();
        Assert.AreEqual(enrollmentsBefore.Count + 1, enrollmentsAfter.Count);
        CollectionAssert.AreEqual(
            enrollmentsBefore.Select(Snapshot).ToArray(),
            enrollmentsAfter.Where(x => x.UserId != user.Id).Select(Snapshot).ToArray());
        CollectionAssert.AreEqual(passesBefore, passesAfter);
        Assert.AreEqual(5, await RemainingVisitsAsync(app, other));
    }

    [TestMethod]
    public async Task Enroll_UnknownField_IgnoredAndReturns201()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(
            client,
            $"{{\"classId\":{classId},\"seats\":2,\"userId\":1,\"extra\":{{\"a\":[1]}}}}");

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.SingleOrDefaultAsync(x => x.ClassId == classId);
        Assert.IsNotNull(enrollment);
        Assert.AreEqual(user.Id, enrollment.UserId);
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_ClockWithSubMicrosecondTicks_CreatedAtTruncatedToMicroseconds()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        var start = app.Clock.GetUtcNow().UtcDateTime;
        app.Clock.Advance(TimeSpan.FromTicks(1234567));
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}");

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(
            start.ToString("yyyy-MM-ddTHH:mm:ss") + ".123456Z",
            body.RootElement.GetProperty("createdAt").GetString());
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(start.AddTicks(1234560), enrollment.CreatedAt);
    }

    [TestMethod]
    public async Task Enroll_TwiceOnSameClassWithDifferentKeys_CreatesTwoEnrollments()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var first = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", "key-1");
        using var second = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":2}}", "key-2");

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        await using var context = app.CreateContext();
        var enrollments = await context.Enrollments
            .Where(x => x.UserId == user.Id)
            .OrderBy(x => x.Id)
            .Select(x => new { x.ClassId, x.Seats, x.Status })
            .ToListAsync();
        CollectionAssert.AreEqual(
            new[] { new { ClassId = classId, Seats = 1, Status = "active" }, new { ClassId = classId, Seats = 2, Status = "active" } },
            enrollments);
        Assert.AreEqual(7, await RemainingVisitsAsync(app, user));
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
    public async Task Enroll_InvalidBody_Returns400WithoutTrace(string json, params string[] errorKeys)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, json.Replace("CLASS", classId.ToString()));

        await InputRulesTests.AssertValidationProblemAsync(response, errorKeys);
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Enroll_NoBodyNoContentType_Returns400WithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        using var client = await app.CreateClientAsync(user.Email);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments");
        request.Headers.Add("Idempotency-Key", "key");

        using var response = await client.SendAsync(request);

        await InputRulesTests.AssertValidationProblemAsync(response, string.Empty);
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("   \t ")]
    public async Task Enroll_MissingOrBlankIdempotencyKey_Returns400WithoutTrace(string? key)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", key);

        await InputRulesTests.AssertValidationProblemAsync(response, "Idempotency-Key");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Enroll_MissingKeyAndInvalidSeats_Returns400WithBothErrors()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":0}}", null);

        await InputRulesTests.AssertValidationProblemAsync(response, "Idempotency-Key", "Seats");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Enroll_MissingClassWithInvalidSeats_Returns400Not404()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, "{\"classId\":999999999,\"seats\":0}");

        await InputRulesTests.AssertValidationProblemAsync(response, "Seats");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Enroll_NoToken_Returns401WithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var classId = await app.Data.ClassAsync();
        using var client = app.CreateClient();

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoEnrollmentsAsync(app, classId);
    }

    [TestMethod]
    public async Task Enroll_TokenWithWrongSignature_Returns401WithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ForgedToken(app, user.Id));

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoTraceAsync(app, user, 10);
        await AssertNoEnrollmentsAsync(app, classId);
    }

    [TestMethod]
    [DataRow(999999999L)]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public async Task Enroll_ClassNotFound_Returns404WithoutTrace(long classId)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}");

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
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Enroll_CancelledClass_Returns409ClassCancelledWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(cancelled: true);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}");

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, text);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        CollectionAssert.AreEqual(
            new[] { "reason", "status", "title", "traceId", "type" },
            root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.AreEqual(409, root.GetProperty("status").GetInt32());
        Assert.AreEqual("Conflict", root.GetProperty("title").GetString());
        Assert.AreEqual("https://tools.ietf.org/html/rfc9110#section-15.5.10", root.GetProperty("type").GetString());
        Assert.AreEqual("class-cancelled", root.GetProperty("reason").GetString());
        StringAssert.Matches(root.GetProperty("traceId").GetString(), new Regex(TraceParent));
        await AssertNoTraceAsync(app, user, 10);
        await AssertNoEnrollmentsAsync(app, classId);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string json, string? key = "key")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    private static async Task<int> RemainingVisitsAsync(TestApp app, TestUser user)
    {
        await using var context = app.CreateContext();

        return await context.Passes.Where(x => x.UserId == user.Id).Select(x => x.RemainingVisits).SingleAsync();
    }

    private static async Task AssertNoTraceAsync(TestApp app, TestUser user, int remainingVisits)
    {
        await using var context = app.CreateContext();

        Assert.IsFalse(await context.Enrollments.AnyAsync(x => x.UserId == user.Id));
        Assert.AreEqual(remainingVisits, await RemainingVisitsAsync(app, user));
    }

    private static async Task AssertNoEnrollmentsAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();

        Assert.IsFalse(await context.Enrollments.AnyAsync(x => x.ClassId == classId));
    }

    private static string Snapshot(DataAccessLayer.Entities.Enrollment x)
    {
        return $"{x.Id}|{x.UserId}|{x.ClassId}|{x.Seats}|{x.Status}|{x.CreatedAt:O}|{x.CancelledAt:O}";
    }

    private static string ForgedToken(TestApp app, long userId)
    {
        var options = app.Services.GetRequiredService<JwtOptions>();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('x', 64)));
        var token = new JwtSecurityToken(
            options.Issuer,
            options.Audience,
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, "user")],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
