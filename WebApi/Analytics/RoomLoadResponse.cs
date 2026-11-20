namespace WebApi.Analytics;

public sealed record RoomLoadResponse(IReadOnlyList<RoomLoadItemResponse> Items);

public sealed record RoomLoadItemResponse(DateOnly Date, RoomLoadRoomResponse Room, int ClassesCount, int TotalCapacity, int BookedSeats);

public sealed record RoomLoadRoomResponse(long Id, string Name);
