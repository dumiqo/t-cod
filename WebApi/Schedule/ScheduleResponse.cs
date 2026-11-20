namespace WebApi.Schedule;

public sealed record ScheduleResponse(IReadOnlyList<ScheduleItemResponse> Items, string? ContinuationToken);

public sealed record ScheduleItemResponse(
    long ClassId,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    ScheduleRoomResponse Room,
    int Capacity,
    int FreeSeats,
    int MySeats);

public sealed record ScheduleRoomResponse(long Id, string Name);
