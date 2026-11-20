using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Contract;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.ChangeSeatsApi;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class ChangeSeatsTests
{
    [TestMethod]
    public async Task ChangeSeats_Increase_Returns200WithCardAndChargesDifference()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 7);
        var classId = await app.Data.ClassAsync(capacity: 10);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        var before = await EnrollmentAsync(app, id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 5);

        var body = await AssertCardAsync(client, response, id);
        Assert.AreEqual(5, body["seats"]!.GetValue<int>());
        Assert.AreEqual("active", body["status"]!.GetValue<string>());
        Assert.AreEqual(4, body["pass"]!["remainingVisits"]!.GetValue<int>());
        var after = await EnrollmentAsync(app, id);
        Assert.AreEqual(5, after.Seats);
        Assert.AreEqual("active", after.Status);
        Assert.AreEqual(before.CreatedAt, after.CreatedAt);
        Assert.IsNull(after.CancelledAt);
        Assert.IsNull(after.RefundedVisits);
        Assert.AreEqual(4, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task ChangeSeats_IncreaseToCapacity_ClassBecomesFull()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var other = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 5);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);
        using var otherClient = await app.CreateClientAsync(other.Email);

        using var response = await ChangeSeatsAsync(client, id, 5);
        using var enroll = await EnrollAsync(otherClient, classId, 1);

        await AssertCardAsync(client, response, id);
        await AssertConflictAsync(enroll, "capacity-exceeded");
        Assert.AreEqual(5, await ActiveSeatsAsync(app, classId));
        Assert.AreEqual(2, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, other));
    }

    [TestMethod]
    public async Task ChangeSeats_Decrease_RefundsDifferenceAndFreesSeats()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 1);
        var other = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(capacity: 5);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 5);
        using var client = await app.CreateClientAsync(user.Email);
        using var otherClient = await app.CreateClientAsync(other.Email);

        using var response = await ChangeSeatsAsync(client, id, 2);

        var body = await AssertCardAsync(client, response, id);
        Assert.AreEqual(2, body["seats"]!.GetValue<int>());
        Assert.AreEqual(4, body["pass"]!["remainingVisits"]!.GetValue<int>());
        var after = await EnrollmentAsync(app, id);
        Assert.AreEqual(2, after.Seats);
        Assert.AreEqual("active", after.Status);
        Assert.IsNull(after.CancelledAt);
        Assert.IsNull(after.RefundedVisits);
        Assert.AreEqual(4, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(2, await ActiveSeatsAsync(app, classId));

        using var tooMany = await EnrollAsync(otherClient, classId, 4);
        using var exact = await EnrollAsync(otherClient, classId, 3);

        await AssertConflictAsync(tooMany, "capacity-exceeded");
        Assert.AreEqual(HttpStatusCode.Created, exact.StatusCode, await exact.Content.ReadAsStringAsync());
        Assert.AreEqual(5, await ActiveSeatsAsync(app, classId));
        Assert.AreEqual(7, await RemainingVisitsAsync(app, other));
    }

    [TestMethod]
    public async Task ChangeSeats_SameSeats_Returns200AndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 6);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 3);
        var snapshot = await SnapshotAsync(app, id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 3);

        var body = await AssertCardAsync(client, response, id);
        Assert.AreEqual(3, body["seats"]!.GetValue<int>());
        Assert.AreEqual(6, body["pass"]!["remainingVisits"]!.GetValue<int>());
        Assert.AreEqual(snapshot, await SnapshotAsync(app, id));
        Assert.AreEqual(6, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task ChangeSeats_UnknownFieldsInBody_IgnoresThemAndReturns200()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 6);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsApi.PostAsync(client, id.ToString(), "{\"seats\":2,\"classId\":1,\"status\":\"cancelled\"}");

        await AssertCardAsync(client, response, id);
        await AssertNoChangeTraceAsync(app, user, id, 2, 5);
    }

    [TestMethod]
    [DataRow("{\"seats\":0}", "Seats")]
    [DataRow("{\"seats\":-1}", "Seats")]
    [DataRow("{\"seats\":-2147483648}", "Seats")]
    [DataRow("{}", "Seats")]
    [DataRow("{\"seats\":null}", "Seats")]
    [DataRow("{\"seats\":\"2\"}", "$.seats", "request")]
    [DataRow("{\"seats\":1.5}", "$.seats", "request")]
    [DataRow("{\"seats\":true}", "$.seats", "request")]
    [DataRow("{\"seats\":2147483648}", "$.seats", "request")]
    [DataRow("{\"seats\":", "$.seats", "request")]
    [DataRow("{", "$", "request")]
    [DataRow("not json", "$", "request")]
    [DataRow("[]", "$", "request")]
    [DataRow("", "")]
    [DataRow("null", "")]
    public async Task ChangeSeats_InvalidBody_Returns400WithoutTrace(string json, params string[] errorKeys)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsApi.PostAsync(client, id.ToString(), json);

        await InputRulesTests.AssertValidationProblemAsync(response, errorKeys);
        await AssertNoChangeTraceAsync(app, user, id, 2, 5);
    }

    [TestMethod]
    public async Task ChangeSeats_NoBodyNoContentType_Returns400WithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.PostAsync($"/enrollments/{id}/seats", null);

        await InputRulesTests.AssertValidationProblemAsync(response, string.Empty);
        await AssertNoChangeTraceAsync(app, user, id, 2, 5);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("1.5")]
    [DataRow("9223372036854775808")]
    public async Task ChangeSeats_IdNotLong_Returns400WithIdError(string id)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var enrollmentId = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsApi.PostAsync(client, id, "{\"seats\":3}");

        await InputRulesTests.AssertValidationProblemAsync(response, "id");
        await AssertNoChangeTraceAsync(app, user, enrollmentId, 2, 5);
    }

    [TestMethod]
    public async Task ChangeSeats_NoToken_Returns401WithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = app.CreateClient();

        using var response = await ChangeSeatsAsync(client, id, 3);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
        await AssertNoChangeTraceAsync(app, user, id, 2, 5);
    }

    [TestMethod]
    [DataRow(999999999L)]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public async Task ChangeSeats_UnknownId_Returns404(long id)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var enrollmentId = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsApi.PostAsync(client, id.ToString(), "{\"seats\":3}");

        await AssertNotFoundAsync(response);
        await AssertNoChangeTraceAsync(app, user, enrollmentId, 2, 5);
    }

    [TestMethod]
    public async Task ChangeSeats_OtherUsersEnrollment_Returns404AndLeavesNoTrace()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var user = await app.Data.UserAsync(remainingVisits: 6);
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 3);

        await AssertNotFoundAsync(response);
        await AssertNoChangeTraceAsync(app, owner, id, 2, 5);
        Assert.AreEqual(6, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task ChangeSeats_AdminOnOtherUsersEnrollment_Returns404AndLeavesNoTrace()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await ChangeSeatsAsync(client, id, 1);

        await AssertNotFoundAsync(response);
        await AssertNoChangeTraceAsync(app, owner, id, 2, 5);
    }

    [TestMethod]
    public async Task ChangeSeats_MissingAndOthers_SameBodyExceptTraceId()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var others = await ChangeSeatsAsync(client, id, 2);
        using var missing = await ChangeSeatsAsync(client, id + 1000000, 2);

        await AssertNotFoundAsync(others);
        await AssertNotFoundAsync(missing);
        Assert.AreEqual(missing.Content.Headers.ContentType?.ToString(), others.Content.Headers.ContentType?.ToString());
        var othersBody = JsonNode.Parse(await others.Content.ReadAsStringAsync())!.AsObject();
        var missingBody = JsonNode.Parse(await missing.Content.ReadAsStringAsync())!.AsObject();
        othersBody.Remove("traceId");
        missingBody.Remove("traceId");
        Assert.IsTrue(JsonNode.DeepEquals(missingBody, othersBody), $"{missingBody} vs {othersBody}");
    }

    [TestMethod]
    [DataRow(-1, 3, DisplayName = "pass expired yesterday")]
    [DataRow(30, 0, DisplayName = "no visits left")]
    public async Task ChangeSeats_DecreaseWithUnusablePass_Returns200AndRefunds(int validDays, int remainingVisits)
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime);
        var user = await app.Data.UserAsync(remainingVisits, today.AddDays(validDays));
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 4);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 1);

        var body = await AssertCardAsync(client, response, id);
        Assert.AreEqual(remainingVisits + 3, body["pass"]!["remainingVisits"]!.GetValue<int>());
        await AssertNoChangeTraceAsync(app, user, id, 1, remainingVisits + 3);
    }

    [TestMethod]
    public async Task ChangeSeats_DecreaseFromMaxSeatsToOne_RefundsIntMaxMinusOne()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 0);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(capacity: int.MaxValue), int.MaxValue);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 1);

        var body = await AssertCardAsync(client, response, id);
        Assert.AreEqual(int.MaxValue - 1, body["pass"]!["remainingVisits"]!.GetValue<int>());
        await AssertNoChangeTraceAsync(app, user, id, 1, int.MaxValue - 1);
    }

    [TestMethod]
    public async Task ChangeSeats_OneOfTwoEnrollmentsOnSameClass_ChangesOnlyIt()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync();
        var first = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        var second = await app.Data.EnrollmentAsync(user.Id, classId, 4);
        var secondBefore = await SnapshotAsync(app, second);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, first, 3);

        var body = await AssertCardAsync(client, response, first);
        Assert.AreEqual(first, body["id"]!.GetValue<long>());
        Assert.AreEqual(secondBefore, await SnapshotAsync(app, second));
        await AssertNoChangeTraceAsync(app, user, first, 3, 4);
    }

    private static async Task<JsonNode> AssertCardAsync(HttpClient client, HttpResponseMessage response, long id)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        using var card = await client.GetAsync($"/enrollments/{id}");
        var expected = JsonNode.Parse(await card.Content.ReadAsStringAsync());
        var actual = JsonNode.Parse(text)!;
        Assert.IsTrue(JsonNode.DeepEquals(expected, actual), $"expected {expected}, got {actual}");

        return actual;
    }

    private static async Task<long> ActiveSeatsAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();

        return await context.Enrollments
            .Where(x => x.ClassId == classId && x.Status == "active")
            .SumAsync(x => (long)x.Seats);
    }
}
