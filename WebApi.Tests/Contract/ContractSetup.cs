using System.Text;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Analytics;
using WebApi.Tests.Enrollments;
using WebApi.Tests.Infrastructure;
using WebApi.Tests.Schedule;
using WebApi.Tests.Waitlist;

namespace WebApi.Tests.Contract;

public sealed class ContractSetup
{
    public const string Enroll = "POST /enrollments";
    public const string ChangeSeats = "POST /enrollments/{id}/seats";
    public const string Cancel = "POST /enrollments/{id}/cancel";
    public const string Card = "GET /enrollments/{id}";
    public const string GetSchedule = "GET /schedule";
    public const string RoomLoad = "GET /analytics/load";
    public const string Join = "POST /enrollments/waitlist";
    public const string Leave = "POST /enrollments/waitlist/{id}/cancel";

    private static readonly int[] SignalEvents = [1001, 1004, 2001, 2002, 2003, 2004, 2005, 3001, 3002, 3003, 3004];

    private static readonly string[] SignalInstruments =
    [
        "club.enrollments.created",
        "club.visits.charged",
        "club.visits.refunded",
        "club.idempotency.replayed",
        "club.waitlist.joined",
        "club.waitlist.left",
        "club.waitlist.promoted"
    ];

    private ContractSetup(TestApp app, TestUser user)
    {
        App = app;
        User = user;
    }

    public TestApp App { get; }

    public TestUser User { get; }

    public long RoomId { get; private init; }

    public string RoomName { get; private init; } = string.Empty;

    public long EnrollClassId { get; private init; }

    public long ClassId { get; private init; }

    public DateTime ClassStartsAt { get; private init; }

    public long EnrollmentId { get; private init; }

    public long JoinClassId { get; private init; }

    public long LeaveClassId { get; private init; }

    public long EntryId { get; private init; }

    public DateTime From => App.Clock.GetUtcNow().UtcDateTime;

    public DateTime To => From.AddDays(2);

    private long[] ClassIds => [EnrollClassId, ClassId, JoinClassId, LeaveClassId];

    public static async Task<ContractSetup> CreateAsync(TestApp app, TestUser user)
    {
        var roomName = $"Contract room {Guid.NewGuid():N}";
        var roomId = await app.Data.RoomAsync(roomName);
        var startsAt = app.Clock.GetUtcNow().UtcDateTime.AddDays(1);
        var classId = await app.Data.ClassAsync(startsAt: startsAt, roomId: roomId);
        var leaveClassId = await WaitlistApi.FullClassAsync(app);

        return new ContractSetup(app, user)
        {
            RoomId = roomId,
            RoomName = roomName,
            EnrollClassId = await app.Data.ClassAsync(),
            ClassId = classId,
            ClassStartsAt = startsAt,
            EnrollmentId = await app.Data.EnrollmentAsync(user.Id, classId, 1),
            JoinClassId = await WaitlistApi.FullClassAsync(app),
            LeaveClassId = leaveClassId,
            EntryId = await WaitlistApi.EntryAsync(app, user.Id, leaveClassId, 1)
        };
    }

    public HttpRequestMessage Request(string endpoint)
    {
        return endpoint switch
        {
            Enroll => Post("/enrollments", $"{{\"classId\":{EnrollClassId},\"seats\":1}}", Guid.NewGuid().ToString()),
            ChangeSeats => Post($"/enrollments/{EnrollmentId}/seats", "{\"seats\":2}"),
            Cancel => Post($"/enrollments/{EnrollmentId}/cancel"),
            Card => new HttpRequestMessage(HttpMethod.Get, $"/enrollments/{EnrollmentId}"),
            GetSchedule => new HttpRequestMessage(
                HttpMethod.Get,
                $"/schedule?from={ScheduleApi.Iso(From)}&to={ScheduleApi.Iso(To)}&roomId={RoomId}"),
            RoomLoad => new HttpRequestMessage(
                HttpMethod.Get,
                $"/analytics/load?from={RoomLoadApi.Date(From)}&to={RoomLoadApi.Date(To)}&roomId={RoomId}"),
            Join => Post("/enrollments/waitlist", $"{{\"classId\":{JoinClassId},\"seats\":1}}", Guid.NewGuid().ToString()),
            Leave => Post($"/enrollments/waitlist/{EntryId}/cancel"),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };
    }

    public static HttpRequestMessage Post(string path, string? json = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    public async Task<string> TraceAsync()
    {
        await using var context = App.CreateContext();
        var parts = new List<string>();
        parts.AddRange(await context.Passes.AsNoTracking()
            .Where(x => x.UserId == User.Id)
            .Select(x => x.Id + "|" + x.RemainingVisits + "|" + x.ValidUntil)
            .ToListAsync());
        parts.AddRange(await context.IdempotencyKeys.AsNoTracking()
            .Where(x => x.UserId == User.Id)
            .OrderBy(x => x.Id)
            .Select(x => x.Id + "|" + x.ClassId + "|" + x.Seats + "|" + x.ResultId)
            .ToListAsync());
        foreach (var classId in ClassIds)
        {
            parts.AddRange(await EnrollApi.EnrollmentsSnapshotAsync(App, classId));
            parts.AddRange(await WaitlistApi.WaitlistSnapshotAsync(App, classId));
        }

        var enrollments = await context.Enrollments.AsNoTracking().Where(x => x.UserId == User.Id).OrderBy(x => x.Id).ToListAsync();
        parts.AddRange(enrollments.Select(EnrollApi.Snapshot));
        parts.Add("waiting " + await context.WaitlistEntries.CountAsync(x => x.UserId == User.Id));
        parts.Add("events " + App.Logs.GetSnapshot().Count(x => SignalEvents.Contains(x.Id.Id)));
        parts.AddRange(SignalInstruments.Select(x => x + " " + EnrollApi.MetricSum(App, x)));

        return string.Join("\n", parts);
    }
}
