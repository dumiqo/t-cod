using System.Net;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Enrollments;
using WebApi.Tests.Infrastructure;
using WebApi.Tests.Schedule;
using WebApi.Tests.Waitlist;

namespace WebApi.Tests.Contract;

[TestClass]
public class IdentityFromTokenTests
{
    [TestMethod]
    public async Task Enroll_ForeignUserAndUnknownFieldsInBody_EnrollsTokenUser()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var otherBefore = await StateAsync(app, other);

        using var response = await EnrollApi.PostAsync(client, $"{{\"classId\":{classId},\"seats\":2,{Extra(other, 12345)}}}");

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, text);
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.SingleAsync(x => x.ClassId == classId);
        Assert.AreEqual(user.Id, enrollment.UserId);
        Assert.AreEqual(2, enrollment.Seats);
        Assert.AreEqual("active", enrollment.Status);
        Assert.AreNotEqual(12345, enrollment.Id);
        Assert.AreEqual(8, await EnrollApi.RemainingVisitsAsync(app, user));
        Assert.AreEqual(otherBefore, await StateAsync(app, other));
    }

    [TestMethod]
    public async Task ChangeSeats_ForeignUserAndUnknownFieldsInBody_ChangesTokenUserEnrollment()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 1);
        var otherId = await app.Data.EnrollmentAsync(other.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);
        var otherBefore = await StateAsync(app, other);

        using var response = await client.SendAsync(
            ContractSetup.Post($"/enrollments/{id}/seats", $"{{\"seats\":3,{Extra(other, otherId)}}}"));

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.AreEqual(user.Id, enrollment.UserId);
        Assert.AreEqual(3, enrollment.Seats);
        Assert.AreEqual("active", enrollment.Status);
        Assert.AreEqual(8, await EnrollApi.RemainingVisitsAsync(app, user));
        Assert.AreEqual(otherBefore, await StateAsync(app, other));
    }

    [TestMethod]
    public async Task Join_ForeignUserAndUnknownFieldsInBody_QueuesTokenUser()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await WaitlistApi.FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        var otherBefore = await StateAsync(app, other);

        using var response = await WaitlistApi.PostAsync(client, $"{{\"classId\":{classId},\"seats\":2,{Extra(other, 12345)}}}");

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, text);
        await using var context = app.CreateContext();
        var entry = await context.WaitlistEntries.SingleAsync(x => x.ClassId == classId);
        Assert.AreEqual(user.Id, entry.UserId);
        Assert.AreEqual(2, entry.Seats);
        Assert.AreEqual("waiting", entry.Status);
        Assert.AreNotEqual(12345, entry.Id);
        Assert.AreEqual(10, await EnrollApi.RemainingVisitsAsync(app, user));
        Assert.AreEqual(otherBefore, await StateAsync(app, other));
    }

    [TestMethod]
    public async Task Schedule_ForeignUserIdInQuery_MySeatsOfTokenUser()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var other = await app.Data.UserAsync();
        var roomName = $"Identity room {Guid.NewGuid():N}";
        var roomId = await app.Data.RoomAsync(roomName);
        var from = app.Clock.GetUtcNow().UtcDateTime;
        var startsAt = from.AddDays(1);
        var classId = await app.Data.ClassAsync(startsAt: startsAt, roomId: roomId);
        await app.Data.EnrollmentAsync(user.Id, classId, 1);
        await app.Data.EnrollmentAsync(other.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ScheduleApi.GetAsync(client, from, from.AddDays(2), $"&roomId={roomId}&userId={other.Id}");

        await ScheduleApi.AssertPageAsync(
            response,
            ScheduleApi.Item(classId, startsAt, roomId, roomName, freeSeats: 6, mySeats: 1));
    }

    private static string Extra(TestUser other, long id)
    {
        return $"\"userId\":{other.Id},\"id\":{id},\"status\":\"cancelled\",\"extra\":{{\"userId\":{other.Id},\"seats\":7}}";
    }

    private static async Task<string> StateAsync(TestApp app, TestUser user)
    {
        await using var context = app.CreateContext();
        var parts = new List<string> { (await EnrollApi.RemainingVisitsAsync(app, user)).ToString() };
        var enrollments = await context.Enrollments.AsNoTracking().Where(x => x.UserId == user.Id).OrderBy(x => x.Id).ToListAsync();
        parts.AddRange(enrollments.Select(EnrollApi.Snapshot));
        parts.Add("waiting " + await context.WaitlistEntries.CountAsync(x => x.UserId == user.Id));
        parts.Add("keys " + await context.IdempotencyKeys.CountAsync(x => x.UserId == user.Id));

        return string.Join("\n", parts);
    }
}
