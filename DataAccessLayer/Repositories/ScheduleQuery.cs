namespace DataAccessLayer.Repositories;

public sealed record ScheduleQuery(DateTime From, DateTime To, long? RoomId, int Take, ScheduleCursor? After = null);

public sealed record ScheduleCursor(DateTime StartsAt, long Id);
