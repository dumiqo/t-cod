using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using WebApi.Time;

namespace WebApi.Schedule;

public sealed class ScheduleService
{
    private readonly IUnitOfWork _unitOfWork;

    public ScheduleService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<ScheduleResponse> GetPageAsync(
        DateTime from,
        DateTime to,
        long? roomId,
        int limit,
        ContinuationToken? continuationToken,
        long userId,
        CancellationToken ct)
    {
        var query = new ScheduleQuery(Bound(from), Bound(to), roomId, limit + 1, continuationToken?.After);
        var rows = await _unitOfWork.Class.GetSchedulePageAsync(query, userId, ct);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit
            ? new ContinuationToken(query.From, query.To, roomId, new ScheduleCursor(page[^1].StartsAt, page[^1].Id)).Encode()
            : null;

        var items = page
            .Select(x => new ScheduleItemResponse(
                x.Id,
                x.Title,
                x.StartsAt,
                x.DurationMinutes,
                new ScheduleRoomResponse(x.RoomId, x.RoomName),
                x.Capacity,
                (int)Math.Max(0, x.Capacity - x.TakenSeats),
                // решение: насыщение, а не 500 на всю страницу: больше int.MaxValue сумма бывает только при правке базы мимо API.
                (int)Math.Min(x.MySeats, int.MaxValue)))
            .ToList();

        return new ScheduleResponse(items, next);
    }

    // решение: границы округляются вверх до микросекунды: Npgsql отбрасывает доли меньше микросекунды,
    // а starts_at выровнен по микросекунде, поэтому после округления вверх граница остаётся точной.
    public static DateTime Bound(DateTime value)
    {
        return value.CeilingToMicroseconds();
    }
}
