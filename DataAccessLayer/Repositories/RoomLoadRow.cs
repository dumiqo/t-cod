namespace DataAccessLayer.Repositories;

public sealed record RoomLoadRow(
    DateOnly Date,
    long RoomId,
    string RoomName,
    int ClassesCount,
    long TotalCapacity,
    long BookedSeats);
