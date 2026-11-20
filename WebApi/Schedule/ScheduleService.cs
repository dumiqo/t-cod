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

    public async Task<ScheduleResponse> GetPageAsync(DateTime from, DateTime to, long? roomId, int limit, long userId, CancellationToken ct)
    {
        // решение: границы округляются вверх до микросекунды: Npgsql отбрасывает доли меньше микросекунды,
        // а starts_at выровнен по микросекунде, поэтому после округления вверх граница остаётся точной.
        var query = new ScheduleQuery(from.CeilingToMicroseconds(), to.CeilingToMicroseconds(), roomId, limit);
        var rows = await _unitOfWork.Class.GetSchedulePageAsync(query, userId, ct);

        var items = rows
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

        return new ScheduleResponse(items, null);
    }
}
