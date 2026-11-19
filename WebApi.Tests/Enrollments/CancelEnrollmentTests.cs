using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Contract;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class CancelEnrollmentTests
{
    [TestMethod]
    public async Task Cancel_MoreThanTwoHoursBeforeStart_Returns200AndRefundsAllVisits()
    {
        await using var app = await TestApp.StartAsync();
        var start = app.Clock.GetUtcNow().UtcDateTime;
        var user = await app.Data.UserAsync(remainingVisits: 7);
        var classId = await app.Data.ClassAsync(startsAt: start.AddDays(1));
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        app.Clock.Advance(TimeSpan.FromTicks(1234567));
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 3);
        var enrollment = await EnrollmentAsync(app, id);
        Assert.AreEqual("cancelled", enrollment.Status);
        Assert.AreEqual(start.AddTicks(1234560), enrollment.CancelledAt);
        Assert.AreEqual(3, enrollment.RefundedVisits);
        Assert.AreEqual(3, enrollment.Seats);
        Assert.AreEqual(start, enrollment.CreatedAt);
        Assert.AreEqual(user.Id, enrollment.UserId);
        Assert.AreEqual(classId, enrollment.ClassId);
        Assert.AreEqual(10, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_FullClass_FreesSeatsForAnotherClient()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var other = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 3);
        var id = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        using var ownerClient = await app.CreateClientAsync(owner.Email);
        using var otherClient = await app.CreateClientAsync(other.Email);
        using var full = await EnrollAsync(otherClient, classId, 3);
        await AssertConflictAsync(full, "capacity-exceeded");

        using var response = await CancelAsync(ownerClient, id);
        using var enroll = await EnrollAsync(otherClient, classId, 3);

        await AssertCancelledAsync(response, id, 3);
        Assert.AreEqual(HttpStatusCode.Created, enroll.StatusCode, await enroll.Content.ReadAsStringAsync());
        Assert.AreEqual(2, await RemainingVisitsAsync(app, other));
    }

    [TestMethod]
    public async Task Cancel_WithJsonBody_IgnoresBodyAndReturns200()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 0);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.PostAsync(
            $"/enrollments/{id}/cancel",
            new StringContent("{\"seats\":1,\"refundedVisits\":0}", Encoding.UTF8, "application/json"));

        await AssertCancelledAsync(response, id, 2);
        Assert.AreEqual(2, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_OneOfTwoEnrollmentsOnSameClass_CancelsOnlyIt()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 1);
        var classId = await app.Data.ClassAsync();
        var first = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        var second = await app.Data.EnrollmentAsync(user.Id, classId, 4);
        var secondBefore = await SnapshotAsync(app, second);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, first);

        await AssertCancelledAsync(response, first, 2);
        Assert.AreEqual(secondBefore, await SnapshotAsync(app, second));
        Assert.AreEqual("cancelled", (await EnrollmentAsync(app, first)).Status);
        Assert.AreEqual(3, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_Repeated_ReturnsSameResultAndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 3);
        using var client = await app.CreateClientAsync(user.Email);
        using var first = await CancelAsync(client, id);
        var firstText = await first.Content.ReadAsStringAsync();
        var snapshot = await SnapshotAsync(app, id);
        app.Clock.Advance(TimeSpan.FromHours(1));

        using var repeat = await CancelAsync(client, id);

        await AssertCancelledAsync(repeat, id, 3);
        Assert.AreEqual(firstText, await repeat.Content.ReadAsStringAsync());
        Assert.AreEqual(snapshot, await SnapshotAsync(app, id));
        Assert.AreEqual(7, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task Cancel_AlreadyCancelled_ReturnsStoredRefundAndChangesNothing(int refundedVisits)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(capacity: 2);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2, cancelled: true, refundedVisits: refundedVisits);
        var snapshot = await SnapshotAsync(app, id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, refundedVisits);
        Assert.AreEqual(snapshot, await SnapshotAsync(app, id));
        Assert.AreEqual(4, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_SeededCancelledEnrollment_ReturnsRefundFilledBySeederAndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        TestUser user;
        long id;
        int seats;
        long classId;
        await using (var context = app.CreateContext())
        {
            var enrollment = await context.Enrollments.AsNoTracking()
                .Where(x => x.Status == "cancelled")
                .OrderBy(x => x.Id)
                .FirstAsync();
            var email = await context.Users.Where(x => x.Id == enrollment.UserId).Select(x => x.Email).SingleAsync();
            user = new TestUser(enrollment.UserId, email);
            id = enrollment.Id;
            seats = enrollment.Seats;
            classId = enrollment.ClassId;
        }

        var remaining = await RemainingVisitsAsync(app, user);
        var snapshot = await SnapshotAsync(app, id);
        var occupied = await ActiveSeatsAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, seats);
        Assert.AreEqual(snapshot, await SnapshotAsync(app, id));
        Assert.AreEqual(remaining, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(occupied, await ActiveSeatsAsync(app, classId));
    }

    [TestMethod]
    public async Task Cancel_NoToken_Returns401AndLeavesNoTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = app.CreateClient();

        using var response = await CancelAsync(client, id);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
        await AssertNoCancelTraceAsync(app, id, user, 5);
    }

    [TestMethod]
    public async Task Cancel_InvalidToken_Returns401AndLeavesNoTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        using var response = await CancelAsync(client, id);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
        await AssertNoCancelTraceAsync(app, id, user, 5);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("1.5")]
    [DataRow("9223372036854775808")]
    public async Task Cancel_IdNotLong_Returns400WithIdError(string id)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var enrollmentId = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await InputRulesTests.AssertValidationProblemAsync(response, "id");
        await AssertNoCancelTraceAsync(app, enrollmentId, user, 5);
    }

    [TestMethod]
    [DataRow(999999999L)]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public async Task Cancel_UnknownId_Returns404(long id)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var enrollmentId = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await AssertNotFoundAsync(response);
        await AssertNoCancelTraceAsync(app, enrollmentId, user, 5);
    }

    [TestMethod]
    public async Task Cancel_OtherUsersActiveEnrollment_Returns404AndLeavesNoTrace()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var user = await app.Data.UserAsync(remainingVisits: 6);
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await AssertNotFoundAsync(response);
        await AssertNoCancelTraceAsync(app, id, owner, 5);
        Assert.AreEqual(6, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_OtherUsersCancelledEnrollment_Returns404()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 2, cancelled: true);
        var snapshot = await SnapshotAsync(app, id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await AssertNotFoundAsync(response);
        Assert.AreEqual(snapshot, await SnapshotAsync(app, id));
    }

    [TestMethod]
    public async Task Cancel_AdminOnOtherUsersEnrollment_Returns404AndLeavesNoTrace()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await CancelAsync(client, id);

        await AssertNotFoundAsync(response);
        await AssertNoCancelTraceAsync(app, id, owner, 5);
    }

    [TestMethod]
    public async Task Cancel_MissingAndOthers_SameBodyExceptTraceId()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var others = await CancelAsync(client, id);
        using var missing = await CancelAsync(client, id + 1000000);

        await AssertNotFoundAsync(others);
        await AssertNotFoundAsync(missing);
        Assert.AreEqual(missing.Content.Headers.ContentType?.ToString(), others.Content.Headers.ContentType?.ToString());
        var othersBody = JsonNode.Parse(await others.Content.ReadAsStringAsync())!.AsObject();
        var missingBody = JsonNode.Parse(await missing.Content.ReadAsStringAsync())!.AsObject();
        othersBody.Remove("traceId");
        missingBody.Remove("traceId");
        Assert.IsTrue(JsonNode.DeepEquals(missingBody, othersBody), $"{missingBody} vs {othersBody}");
    }

    private static async Task<long> ActiveSeatsAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();

        return await context.Enrollments
            .Where(x => x.ClassId == classId && x.Status == "active")
            .SumAsync(x => (long)x.Seats);
    }
}
