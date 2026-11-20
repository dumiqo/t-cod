using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Analytics;
using WebApi.Tests.Enrollments;
using WebApi.Tests.Infrastructure;
using WebApi.Tests.Schedule;
using WebApi.Tests.Waitlist;
using static WebApi.Tests.Seeds.SeedQueries;

namespace WebApi.Tests.Seeds;

[TestClass]
public class SeedScenarioTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Enroll_AllFreeSeatsOfSeededClass_Returns201AndFillsClass(int freeSeats)
    {
        await using var app = await TestApp.StartAsync();
        var item = await FreeSeatsClassAsync(app, freeSeats, $"в сиде нет занятия с {freeSeats} свободными местами");
        var user = await ValidUserAsync(app, freeSeats, item.Id);
        var remaining = await EnrollApi.RemainingVisitsAsync(app, user);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollApi.EnrollAsync(client, item.Id, freeSeats);

        var root = await AssertCreatedAsync(response);
        Assert.AreEqual(item.Id, root.GetProperty("classId").GetInt64());
        Assert.AreEqual(freeSeats, root.GetProperty("seats").GetInt32());
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.SingleAsync(x => x.UserId == user.Id && x.ClassId == item.Id);
        Assert.AreEqual(root.GetProperty("id").GetInt64(), enrollment.Id);
        Assert.AreEqual(freeSeats, enrollment.Seats);
        Assert.AreEqual("active", enrollment.Status);
        Assert.AreEqual(item.Capacity, await TakenSeatsAsync(app, item.Id));
        Assert.AreEqual(remaining - freeSeats, await EnrollApi.RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Enroll_MoreThanFreeSeatsOfSeededClass_CapacityExceededWithoutTrace(int freeSeats)
    {
        await using var app = await TestApp.StartAsync();
        var item = await FreeSeatsClassAsync(app, freeSeats, $"в сиде нет занятия с {freeSeats} свободными местами");
        var user = await ValidUserAsync(app, freeSeats + 1, item.Id);
        var before = await StateAsync(app, user, item.Id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollApi.EnrollAsync(client, item.Id, freeSeats + 1);

        await EnrollApi.AssertConflictAsync(response, "capacity-exceeded");
        Assert.AreEqual(before, await StateAsync(app, user, item.Id));
        Assert.AreEqual(item.Capacity - freeSeats, await TakenSeatsAsync(app, item.Id));
    }

    [TestMethod]
    public async Task JoinWaitlist_FullSeededClass_Returns201AtPositionOne()
    {
        await using var app = await TestApp.StartAsync();
        var item = await FreeSeatsClassAsync(app, 0, "в сиде нет занятия без свободных мест");
        var user = await ValidUserAsync(app, 1, item.Id);
        var before = await StateAsync(app, user, item.Id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await WaitlistApi.JoinAsync(client, item.Id, 1);

        var root = await AssertCreatedAsync(response);
        Assert.AreEqual(1, root.GetProperty("position").GetInt32());
        Assert.AreEqual(item.Id, root.GetProperty("classId").GetInt64());
        await using var context = app.CreateContext();
        var entry = await context.WaitlistEntries.SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(root.GetProperty("id").GetInt64(), entry.Id);
        Assert.AreEqual(item.Id, entry.ClassId);
        Assert.AreEqual(1, entry.Seats);
        Assert.AreEqual("waiting", entry.Status);
        Assert.AreEqual(before.Replace("|keys=0|", "|keys=1|"), await StateAsync(app, user, item.Id));
    }

    [TestMethod]
    public async Task JoinWaitlist_SeededClassWithTwoFreeSeats_SeatsAvailable()
    {
        await using var app = await TestApp.StartAsync();
        var item = await FreeSeatsClassAsync(app, 2, "в сиде нет занятия с 2 свободными местами");
        var user = await ValidUserAsync(app, 2, item.Id);
        var before = await StateAsync(app, user, item.Id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await WaitlistApi.JoinAsync(client, item.Id, 2);

        await EnrollApi.AssertConflictAsync(response, "seats-available");
        Assert.AreEqual(before, await StateAsync(app, user, item.Id));
        await using var context = app.CreateContext();
        Assert.IsFalse(await context.WaitlistEntries.AnyAsync(x => x.UserId == user.Id));
    }

    [TestMethod]
    public async Task Enroll_SeededClassInTwentyMinutes_Returns201()
    {
        await using var app = await TestApp.StartAsync();
        var item = await ClassAtAsync(app, Now(app).AddMinutes(20), "в сиде нет занятия через 20 минут");
        var user = await ValidUserAsync(app, 1, item.Id);
        var remaining = await EnrollApi.RemainingVisitsAsync(app, user);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollApi.EnrollAsync(client, item.Id, 1);

        var root = await AssertCreatedAsync(response);
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.SingleAsync(x => x.UserId == user.Id && x.ClassId == item.Id);
        Assert.AreEqual(root.GetProperty("id").GetInt64(), enrollment.Id);
        Assert.AreEqual(1, enrollment.Seats);
        Assert.AreEqual(remaining - 1, await EnrollApi.RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_SeededClassStartingNow_ClassStartedWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var item = await ClassAtAsync(app, Now(app), "в сиде нет занятия, начинающегося в момент засева");
        var user = await ValidUserAsync(app, 1, item.Id);
        var before = await StateAsync(app, user, item.Id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollApi.EnrollAsync(client, item.Id, 1);

        await EnrollApi.AssertConflictAsync(response, "class-started");
        Assert.AreEqual(before, await StateAsync(app, user, item.Id));
    }

    [TestMethod]
    [DataRow(50)]
    [DataRow(80)]
    [DataRow(100)]
    [DataRow(115)]
    public async Task Cancel_SeededEnrollmentWithinTwoHours_Returns200WithoutRefund(int minutes)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddMinutes(minutes);
        var enrollment = await ActiveEnrollmentAsync(app, x => x.Class.StartsAt == startsAt, $"в сиде нет брони на занятие через {minutes} минут");
        var user = await OwnerAsync(app, enrollment.Id);
        var remaining = await EnrollApi.RemainingVisitsAsync(app, user);
        var taken = await TakenSeatsAsync(app, enrollment.ClassId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelApi.CancelAsync(client, enrollment.Id);

        await CancelApi.AssertCancelledAsync(response, enrollment.Id, 0);
        var after = await CancelApi.EnrollmentAsync(app, enrollment.Id);
        Assert.AreEqual("cancelled", after.Status);
        Assert.AreEqual(0, after.RefundedVisits);
        Assert.AreEqual(Now(app), after.CancelledAt);
        Assert.AreEqual(taken - enrollment.Seats, await TakenSeatsAsync(app, enrollment.ClassId));
        Assert.AreEqual(remaining, await EnrollApi.RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_SeededEnrollmentExactlyTwoHoursBefore_Returns200WithFullRefund()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddMinutes(115);
        var enrollment = await ActiveEnrollmentAsync(app, x => x.Class.StartsAt == startsAt, "в сиде нет брони на занятие через 115 минут");
        var user = await OwnerAsync(app, enrollment.Id);
        var remaining = await EnrollApi.RemainingVisitsAsync(app, user);
        app.Clock.AdjustTime(startsAt.AddHours(-2));
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelApi.CancelAsync(client, enrollment.Id);

        await CancelApi.AssertCancelledAsync(response, enrollment.Id, enrollment.Seats);
        var after = await CancelApi.EnrollmentAsync(app, enrollment.Id);
        Assert.AreEqual("cancelled", after.Status);
        Assert.AreEqual(enrollment.Seats, after.RefundedVisits);
        Assert.AreEqual(remaining + enrollment.Seats, await EnrollApi.RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task GetCard_SeededEnrollmentOnCancelledClass_ActiveWithCancelledClass(bool future)
    {
        await using var app = await TestApp.StartAsync();
        var enrollment = await CancelledClassEnrollmentAsync(app, future);
        var user = await OwnerAsync(app, enrollment.Id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.GetAsync($"/enrollments/{enrollment.Id}");

        var card = await AssertCardAsync(response);
        Assert.AreEqual(enrollment.Id, card["id"]!.GetValue<long>());
        Assert.AreEqual("active", card["status"]!.GetValue<string>());
        Assert.AreEqual(enrollment.Seats, card["seats"]!.GetValue<int>());
        Assert.AreEqual(enrollment.ClassId, card["class"]!["id"]!.GetValue<long>());
        Assert.IsTrue(card["class"]!["isCancelled"]!.GetValue<bool>());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChangeSeats_SeededEnrollmentOnCancelledClass_ClassCancelledWithoutTrace(bool future)
    {
        await using var app = await TestApp.StartAsync();
        var enrollment = await CancelledClassEnrollmentAsync(app, future);
        var user = await OwnerAsync(app, enrollment.Id);
        var remaining = await EnrollApi.RemainingVisitsAsync(app, user);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsApi.ChangeSeatsAsync(client, enrollment.Id, enrollment.Seats == 1 ? 2 : 1);

        await EnrollApi.AssertConflictAsync(response, "class-cancelled");
        await ChangeSeatsApi.AssertNoChangeTraceAsync(app, user, enrollment.Id, enrollment.Seats, remaining);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Cancel_SeededEnrollmentOnCancelledClass_Returns200WithFullRefund(bool future)
    {
        await using var app = await TestApp.StartAsync();
        var enrollment = await CancelledClassEnrollmentAsync(app, future);
        var user = await OwnerAsync(app, enrollment.Id);
        var remaining = await EnrollApi.RemainingVisitsAsync(app, user);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelApi.CancelAsync(client, enrollment.Id);

        await CancelApi.AssertCancelledAsync(response, enrollment.Id, enrollment.Seats);
        var after = await CancelApi.EnrollmentAsync(app, enrollment.Id);
        Assert.AreEqual("cancelled", after.Status);
        Assert.AreEqual(enrollment.Seats, after.RefundedVisits);
        Assert.AreEqual(remaining + enrollment.Seats, await EnrollApi.RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Schedule_SeededUserWithTwoEnrollments_MySeatsTwoThenOneAfterCancel()
    {
        await using var app = await TestApp.StartAsync();
        var (first, second) = await DoubleBookingAsync(app);
        var user = await OwnerAsync(app, first.Id);
        var startsAt = first.Class.StartsAt;
        using var client = await app.CreateClientAsync(user.Email);
        var expectedBefore = await ScheduleItemAsync(app, first.ClassId, user.Id);
        using var cardBefore = await client.GetAsync($"/enrollments/{second.Id}");
        var secondBefore = await AssertCardAsync(cardBefore);

        using var scheduleBefore = await ScheduleApi.GetAsync(client, startsAt, startsAt.AddTicks(1), $"&roomId={first.Class.RoomId}");
        using var cancel = await CancelApi.CancelAsync(client, first.Id);
        var expectedAfter = await ScheduleItemAsync(app, first.ClassId, user.Id);
        using var scheduleAfter = await ScheduleApi.GetAsync(client, startsAt, startsAt.AddTicks(1), $"&roomId={first.Class.RoomId}");
        using var cardAfter = await client.GetAsync($"/enrollments/{second.Id}");

        await ScheduleApi.AssertPageAsync(scheduleBefore, expectedBefore);
        Assert.AreEqual(2, JsonSerializer.SerializeToNode(expectedBefore)!["mySeats"]!.GetValue<int>());
        await CancelApi.AssertCancelledAsync(cancel, first.Id, 1);
        await ScheduleApi.AssertPageAsync(scheduleAfter, expectedAfter);
        Assert.AreEqual(1, JsonSerializer.SerializeToNode(expectedAfter)!["mySeats"]!.GetValue<int>());
        var secondAfter = await AssertCardAsync(cardAfter);
        Assert.AreEqual(
            secondBefore["pass"]!["remainingVisits"]!.GetValue<int>() + 1,
            secondAfter["pass"]!["remainingVisits"]!.GetValue<int>());
        secondBefore.Remove("pass");
        secondAfter.Remove("pass");
        Assert.IsTrue(JsonNode.DeepEquals(secondBefore, secondAfter), $"expected {secondBefore}, got {secondAfter}");
        Assert.AreEqual("active", (await CancelApi.EnrollmentAsync(app, second.Id)).Status);
    }

    [TestMethod]
    public async Task Enroll_SeededPassWithoutVisits_PassExhausted()
    {
        await using var app = await TestApp.StartAsync();
        var today = Today(app);
        var item = await FreeSeatsClassAsync(app, 2, "в сиде нет занятия с 2 свободными местами");
        var user = await UserAsync(app, x => x.RemainingVisits == 0 && x.ValidUntil >= today, item.Id, "в сиде нет действующего абонемента без посещений");

        await AssertEnrollRejectedAsync(app, user, item.Id, 1, "pass-exhausted");
    }

    [TestMethod]
    public async Task Enroll_SeededPassWithOneVisit_TwoSeatsExhaustedThenOneSeatCreated()
    {
        await using var app = await TestApp.StartAsync();
        var today = Today(app);
        var item = await FreeSeatsClassAsync(app, 2, "в сиде нет занятия с 2 свободными местами");
        var user = await UserAsync(app, x => x.RemainingVisits == 1 && x.ValidUntil >= today, item.Id, "в сиде нет действующего абонемента с одним посещением");

        await AssertEnrollRejectedAsync(app, user, item.Id, 2, "pass-exhausted");
        using var client = await app.CreateClientAsync(user.Email);
        using var response = await EnrollApi.EnrollAsync(client, item.Id, 1);

        await AssertCreatedAsync(response);
        Assert.AreEqual(0, await EnrollApi.RemainingVisitsAsync(app, user));
        Assert.AreEqual(item.Capacity - 1, await TakenSeatsAsync(app, item.Id));
    }

    [TestMethod]
    public async Task Enroll_SeededPassExpiredYesterday_PassExpired()
    {
        await using var app = await TestApp.StartAsync();
        var yesterday = Today(app).AddDays(-1);
        var item = await FreeSeatsClassAsync(app, 2, "в сиде нет занятия с 2 свободными местами");
        var user = await UserAsync(app, x => x.RemainingVisits >= 1 && x.ValidUntil == yesterday, item.Id, "в сиде нет абонемента, просроченного на день");

        await AssertEnrollRejectedAsync(app, user, item.Id, 1, "pass-expired");
    }

    [TestMethod]
    public async Task Enroll_SeededExpiredPassWithOneVisit_PassExpiredBeforeExhausted()
    {
        await using var app = await TestApp.StartAsync();
        var today = Today(app);
        var item = await FreeSeatsClassAsync(app, 2, "в сиде нет занятия с 2 свободными местами");
        var user = await UserAsync(app, x => x.RemainingVisits == 1 && x.ValidUntil < today, item.Id, "в сиде нет просроченного абонемента с одним посещением");

        await AssertEnrollRejectedAsync(app, user, item.Id, 2, "pass-expired");
    }

    [TestMethod]
    public async Task Schedule_SeededFutureSameStartPair_BothByIdAndOnePerPage()
    {
        await using var app = await TestApp.StartAsync();
        var pair = await SameStartPairAsync(app, false, "в сиде нет будущей пары занятий с одинаковым началом");
        var user = await ValidUserAsync(app, 1, pair[0].Id);
        var startsAt = pair[0].StartsAt;
        using var client = await app.CreateClientAsync(user.Email);
        var expected = new[] { await ScheduleItemAsync(app, pair[0].Id, user.Id), await ScheduleItemAsync(app, pair[1].Id, user.Id) };

        using var all = await ScheduleApi.GetAsync(client, startsAt, startsAt.AddTicks(1));
        using var page1 = await ScheduleApi.GetAsync(client, startsAt, startsAt.AddTicks(1), "&limit=1");
        var (ids1, token) = await ScheduleApi.PageAsync(page1);
        using var page2 = await ScheduleApi.GetAsync(client, startsAt, startsAt.AddTicks(1), $"&limit=1&continuationToken={token}");
        var (ids2, last) = await ScheduleApi.PageAsync(page2);

        await ScheduleApi.AssertPageAsync(all, expected);
        CollectionAssert.AreEqual(new[] { pair[0].Id }, ids1);
        Assert.IsNotNull(token);
        CollectionAssert.AreEqual(new[] { pair[1].Id }, ids2);
        Assert.IsNull(last);
    }

    [TestMethod]
    public async Task Schedule_SeededSameStartPairWithCancelled_OnlyActiveClass()
    {
        await using var app = await TestApp.StartAsync();
        var pair = await SameStartPairAsync(app, true, "в сиде нет пары с одинаковым началом, где одно занятие отменено");
        var active = pair.Single(x => !x.IsCancelled);
        var user = await ValidUserAsync(app, 1, active.Id);
        var startsAt = active.StartsAt;
        using var client = await app.CreateClientAsync(user.Email);
        var expected = await ScheduleItemAsync(app, active.Id, user.Id);

        using var response = await ScheduleApi.GetAsync(client, startsAt, startsAt.AddTicks(1));

        await ScheduleApi.AssertPageAsync(response, expected);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RoomLoad_DayOfSeededSameStartPair_RowPerRoomWithoutCancelled(bool withCancelled)
    {
        await using var app = await TestApp.StartAsync();
        var pair = await SameStartPairAsync(app, withCancelled, "в сиде нет пары занятий с одинаковым началом");
        var day = pair[0].StartsAt.Date;
        var expected = await RoomLoadItemsAsync(app, day);
        using var client = await app.CreateClientAsync("admin001@club.test");

        using var response = await RoomLoadApi.GetAsync(client, day, day);

        await RoomLoadApi.AssertItemsAsync(response, expected);
        var rooms = expected.Select(x => JsonSerializer.SerializeToNode(x)!["room"]!["id"]!.GetValue<long>()).ToArray();
        foreach (var item in pair.Where(x => !x.IsCancelled))
        {
            CollectionAssert.Contains(rooms, item.RoomId);
        }
    }

    private static Task<DataAccessLayer.Entities.Enrollment> CancelledClassEnrollmentAsync(TestApp app, bool future)
    {
        var now = Now(app);

        return future
            ? ActiveEnrollmentAsync(app, x => x.Class.IsCancelled && x.Class.StartsAt > now, "в сиде нет активной брони на будущем отменённом занятии")
            : ActiveEnrollmentAsync(app, x => x.Class.IsCancelled && x.Class.StartsAt <= now, "в сиде нет активной брони на прошедшем отменённом занятии");
    }

    private static async Task<(DataAccessLayer.Entities.Enrollment First, DataAccessLayer.Entities.Enrollment Second)> DoubleBookingAsync(TestApp app)
    {
        var now = Now(app);
        await using var context = app.CreateContext();
        var pair = await context.Enrollments.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Status == "active" && x.Seats == 1 && x.Class.StartsAt > now.AddHours(2))
            .GroupBy(x => new { x.UserId, x.ClassId })
            .Where(x => x.Count() == 2)
            .OrderBy(x => x.Key.UserId)
            .Select(x => x.Key)
            .FirstOrDefaultAsync();

        if (pair is null)
        {
            Assert.Fail("в сиде нет пользователя с двумя бронями по одному месту на одно занятие");
        }

        var both = await context.Enrollments.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Class)
            .Where(x => x.UserId == pair.UserId && x.ClassId == pair.ClassId && x.Status == "active")
            .OrderBy(x => x.Id)
            .ToListAsync();

        return (both[0], both[1]);
    }

    private static async Task AssertEnrollRejectedAsync(TestApp app, TestUser user, long classId, int seats, string reason)
    {
        var before = await StateAsync(app, user, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollApi.EnrollAsync(client, classId, seats);

        await EnrollApi.AssertConflictAsync(response, reason);
        Assert.AreEqual(before, await StateAsync(app, user, classId));
    }

    private static async Task<string> StateAsync(TestApp app, TestUser user, long classId)
    {
        await using var context = app.CreateContext();
        var enrollments = await EnrollApi.EnrollmentsSnapshotAsync(app, classId);
        var mine = await context.Enrollments.CountAsync(x => x.UserId == user.Id);
        var keys = await context.IdempotencyKeys.CountAsync(x => x.UserId == user.Id);
        var remaining = await EnrollApi.RemainingVisitsAsync(app, user);

        return $"{string.Join(";", enrollments)}|mine={mine}|keys={keys}|remaining={remaining}";
    }

    private static async Task<JsonElement> AssertCreatedAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, text);

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonObject> AssertCardAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);

        return JsonNode.Parse(text)!.AsObject();
    }
}
