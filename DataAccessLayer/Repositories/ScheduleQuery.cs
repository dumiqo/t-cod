namespace DataAccessLayer.Repositories;

public sealed record ScheduleQuery(DateTime From, DateTime To, long? RoomId, int Take);
