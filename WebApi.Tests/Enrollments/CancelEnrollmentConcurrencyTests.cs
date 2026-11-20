using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class CancelEnrollmentConcurrencyTests
{
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    [TestMethod]
    [DoNotParallelize]
    public async Task Cancel_TwoParallelCancelsOfOneEnrollment_RefundOnce()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: Now(app).AddDays(1));
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldClassAsync(app, classId);
        var requests = new[] { CancelAsync(client, id), CancelAsync(client, id) };
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        var responses = await Task.WhenAll(requests);

        Assert.AreEqual(2L, blocked);
        foreach (var response in responses)
        {
            await AssertCancelledAsync(response, id, 3);
            response.Dispose();
        }

        var enrollment = await EnrollmentAsync(app, id);
        Assert.AreEqual("cancelled", enrollment.Status);
        Assert.AreEqual(3, enrollment.RefundedVisits);
        Assert.AreEqual(Now(app), enrollment.CancelledAt);
        Assert.AreEqual(7, await RemainingVisitsAsync(app, user));
        AssertRefundObservedOnce(app, 3);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Cancel_TwoParallelCancelsOnDifferentClasses_BothRefundsReachPass()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var first = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(startsAt: Now(app).AddDays(1)), 2);
        var second = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(startsAt: Now(app).AddDays(2)), 3);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldPassAsync(app, user);
        var requests = new[] { CancelAsync(client, first), CancelAsync(client, second) };
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        var responses = await Task.WhenAll(requests);

        Assert.AreEqual(2L, blocked);
        await AssertCancelledAsync(responses[0], first, 2);
        await AssertCancelledAsync(responses[1], second, 3);
        Assert.AreEqual(9, await RemainingVisitsAsync(app, user));
        Assert.AreEqual("cancelled", (await EnrollmentAsync(app, first)).Status);
        Assert.AreEqual("cancelled", (await EnrollmentAsync(app, second)).Status);
        DisposeAll(responses);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cancel_ClockCrossesTwoHoursWhileWaiting_RefundsNothing(bool holdPass)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = holdPass ? await HoldPassAsync(app, user) : await HoldClassAsync(app, classId);
        var request = CancelAsync(client, id);
        var blocked = await held.WaitForBlockedCountAsync(1);
        app.Clock.SetUtcNow(startsAt - TwoHours + TimeSpan.FromTicks(1));
        await held.DisposeAsync();
        using var response = await request;

        Assert.AreEqual(1L, blocked);
        await AssertCancelledAsync(response, id, 0);
        var enrollment = await EnrollmentAsync(app, id);
        Assert.AreEqual(0, enrollment.RefundedVisits);
        Assert.AreEqual(startsAt - TwoHours, enrollment.CancelledAt);
        Assert.AreEqual(4, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cancel_ClockReachesStartWhileWaiting_Returns409ClassStartedWithoutTrace(bool holdPass)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = holdPass ? await HoldPassAsync(app, user) : await HoldClassAsync(app, classId);
        var request = CancelAsync(client, id);
        var blocked = await held.WaitForBlockedCountAsync(1);
        app.Clock.SetUtcNow(startsAt);
        await held.DisposeAsync();
        using var response = await request;

        Assert.AreEqual(1L, blocked);
        await AssertConflictAsync(response, "class-started");
        await AssertNoCancelTraceAsync(app, id, user, 4);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cancel_LockTimeout_Returns503WithoutTrace(bool holdPass)
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: Now(app).AddDays(1));
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = holdPass ? await HoldPassAsync(app, user) : await HoldClassAsync(app, classId);

        using var response = await CancelAsync(client, id);

        await AssertLockTimeoutAsync(response);
        await AssertNoCancelTraceAsync(app, id, user, 4);
    }

    [TestMethod]
    public async Task Cancel_RepeatWhileClassLocked_Returns200WithStoredResult()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: Now(app).AddDays(1));
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);
        using (var first = await CancelAsync(client, id))
        {
            await AssertCancelledAsync(first, id, 3);
        }

        var before = await SnapshotAsync(app, id);
        await using var held = await HoldClassAsync(app, classId);

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 3);
        Assert.AreEqual(before, await SnapshotAsync(app, id));
        Assert.AreEqual(7, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Cancel_TenParallelCancelsWithoutHeldLock_RefundOnce()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: Now(app).AddDays(1));
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => CancelAsync(client, id)));

        foreach (var response in responses)
        {
            await AssertCancelledAsync(response, id, 3);
        }

        var enrollment = await EnrollmentAsync(app, id);
        Assert.AreEqual(3, enrollment.RefundedVisits);
        Assert.AreEqual(Now(app), enrollment.CancelledAt);
        Assert.AreEqual(7, await RemainingVisitsAsync(app, user));
        AssertRefundObservedOnce(app, 3);
        DisposeAll(responses);
    }

    private static void AssertRefundObservedOnce(TestApp app, int visits)
    {
        var events = RefundedEvents(app);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(visits.ToString(), events[0].GetStructuredStateValue("RefundedVisits"));
        Assert.AreEqual(0, NotRefundedEvents(app).Count);
        var measurements = app.Metrics("club.visits.refunded").GetMeasurementSnapshot();
        Assert.AreEqual(1, measurements.Count);
        Assert.AreEqual(visits, measurements[0].Value);
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }

    private static Task<HeldLock> HoldClassAsync(TestApp app, long classId)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");
    }

    private static Task<HeldLock> HoldPassAsync(TestApp app, TestUser user)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM passes WHERE user_id = {user.Id} FOR UPDATE");
    }

    private static async Task AssertLockTimeoutAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, text);
        Assert.AreEqual("1", response.Headers.GetValues("Retry-After").Single());
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual("lock-timeout", body.RootElement.GetProperty("reason").GetString());
    }

    private static void DisposeAll(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
