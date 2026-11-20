using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Enrollments;
using WebApi.Tests.Infrastructure;
using WebApi.Tests.Schedule;
using static WebApi.Tests.Contract.ContractSetup;

namespace WebApi.Tests.Contract;

[TestClass]
public class AdminAccessTests
{
    [TestMethod]
    [DataRow(Enroll, HttpStatusCode.Created)]
    [DataRow(ChangeSeats, HttpStatusCode.OK)]
    [DataRow(Cancel, HttpStatusCode.OK)]
    [DataRow(Card, HttpStatusCode.OK)]
    [DataRow(GetSchedule, HttpStatusCode.OK)]
    [DataRow(Join, HttpStatusCode.Created)]
    [DataRow(Leave, HttpStatusCode.OK)]
    public async Task Request_AdminOnOwnData_SucceedsLikeUser(string endpoint, HttpStatusCode status)
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.Data.UserAsync(remainingVisits: 10, role: "admin");
        var setup = await CreateAsync(app, admin);
        var now = app.Clock.GetUtcNow().UtcDateTime;
        using var client = await app.CreateClientAsync(admin.Email);
        using var request = setup.Request(endpoint);

        using var response = await client.SendAsync(request);

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(status, response.StatusCode, text);
        if (endpoint == GetSchedule)
        {
            await ScheduleApi.AssertPageAsync(
                response,
                ScheduleApi.Item(setup.ClassId, setup.ClassStartsAt, setup.RoomId, setup.RoomName, freeSeats: 9, mySeats: 1));
            return;
        }

        var body = JsonNode.Parse(text)!;
        await using var context = app.CreateContext();
        switch (endpoint)
        {
            case Enroll:
                var created = await context.Enrollments.SingleAsync(x => x.ClassId == setup.EnrollClassId);
                AssertJson(new { id = created.Id, classId = setup.EnrollClassId, seats = 1, status = "active", createdAt = Seconds(now) }, body);
                Assert.AreEqual(admin.Id, created.UserId);
                Assert.AreEqual(9, await EnrollApi.RemainingVisitsAsync(app, admin));
                break;
            case ChangeSeats:
                Assert.AreEqual(setup.EnrollmentId, body["id"]!.GetValue<long>());
                Assert.AreEqual(2, body["seats"]!.GetValue<int>());
                Assert.AreEqual("active", body["status"]!.GetValue<string>());
                Assert.AreEqual(9, body["pass"]!["remainingVisits"]!.GetValue<int>());
                Assert.AreEqual(2, (await context.Enrollments.SingleAsync(x => x.Id == setup.EnrollmentId)).Seats);
                Assert.AreEqual(9, await EnrollApi.RemainingVisitsAsync(app, admin));
                break;
            case Cancel:
                AssertJson(new { id = setup.EnrollmentId, status = "cancelled", refundedVisits = 1 }, body);
                Assert.AreEqual("cancelled", (await context.Enrollments.SingleAsync(x => x.Id == setup.EnrollmentId)).Status);
                Assert.AreEqual(11, await EnrollApi.RemainingVisitsAsync(app, admin));
                break;
            case Card:
                Assert.AreEqual(setup.EnrollmentId, body["id"]!.GetValue<long>());
                Assert.AreEqual("active", body["status"]!.GetValue<string>());
                Assert.AreEqual(1, body["seats"]!.GetValue<int>());
                Assert.AreEqual(setup.ClassId, body["class"]!["id"]!.GetValue<long>());
                Assert.AreEqual(setup.RoomName, body["class"]!["room"]!["name"]!.GetValue<string>());
                Assert.AreEqual(10, body["pass"]!["remainingVisits"]!.GetValue<int>());
                break;
            case Join:
                var entry = await context.WaitlistEntries.SingleAsync(x => x.ClassId == setup.JoinClassId);
                AssertJson(new { id = entry.Id, classId = setup.JoinClassId, seats = 1, position = 1, createdAt = Seconds(now) }, body);
                Assert.AreEqual(admin.Id, entry.UserId);
                Assert.AreEqual("waiting", entry.Status);
                Assert.AreEqual(10, await EnrollApi.RemainingVisitsAsync(app, admin));
                break;
            case Leave:
                AssertJson(new { id = setup.EntryId, status = "cancelled" }, body);
                Assert.AreEqual("cancelled", (await context.WaitlistEntries.SingleAsync(x => x.Id == setup.EntryId)).Status);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint));
        }
    }

    private static string Seconds(DateTime value)
    {
        return value.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    private static void AssertJson(object expected, JsonNode actual)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(expected));
        Assert.IsTrue(JsonNode.DeepEquals(node, actual), $"expected {node}, got {actual}");
    }
}
