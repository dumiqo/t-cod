using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WebApi.Binding;
using WebApi.Controllers;

namespace WebApi.Analytics;

[ApiController]
[Route("analytics")]
public class AnalyticsController : ClubApiController, IActionFilter
{
    private readonly IUnitOfWork _unitOfWork;

    public AnalyticsController(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    [HttpGet("load")]
    [Authorize(Roles = AdminRole)]
    public async Task<RoomLoadResponse> Load(
        [ModelBinder(typeof(IsoDateModelBinder))] DateOnly from,
        [ModelBinder(typeof(IsoDateModelBinder))] DateOnly to,
        long? roomId,
        CancellationToken ct)
    {
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        // решение: у 9999-12-31 следующего дня нет — граница DateTime.MaxValue, Npgsql шлёт её как infinity; иначе AddDays бросает и выходит 500.
        var end = to == DateOnly.MaxValue ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) : to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var rows = await _unitOfWork.Class.GetRoomLoadAsync(start, end, roomId, ct);

        return new RoomLoadResponse(rows
            .Select(x => new RoomLoadItemResponse(
                x.Date,
                new RoomLoadRoomResponse(x.RoomId, x.RoomName),
                x.ClassesCount,
                // решение: насыщение, как mySeats в расписании: сумма больше int.MaxValue бывает только при правке базы мимо API, 500 на весь отчёт хуже.
                (int)Math.Min(x.TotalCapacity, int.MaxValue),
                (int)Math.Min(x.BookedSeats, int.MaxValue)))
            .ToList());
    }

    void IActionFilter.OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("from", out var from)
            && context.ActionArguments.TryGetValue("to", out var to)
            && from is DateOnly start
            && to is DateOnly end
            && end < start)
        {
            ModelState.AddModelError("to", "The to field must not be earlier than from.");
        }
    }

    void IActionFilter.OnActionExecuted(ActionExecutedContext context)
    {
    }
}
