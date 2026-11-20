namespace DataAccessLayer.Repositories;

public sealed record ScheduleRow(
    long Id,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    int Capacity,
    long RoomId,
    string RoomName);
