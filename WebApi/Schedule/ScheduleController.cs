using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WebApi.Binding;
using WebApi.Controllers;

namespace WebApi.Schedule;

[ApiController]
[Route("schedule")]
public class ScheduleController : ClubApiController, IActionFilter
{
    private readonly ScheduleService _schedule;

    public ScheduleController(ScheduleService schedule)
    {
        _schedule = schedule;
    }

    [HttpGet]
    public Task<ScheduleResponse> Get(
        [ModelBinder(typeof(UtcDateTimeModelBinder))] DateTime from,
        [ModelBinder(typeof(UtcDateTimeModelBinder))] DateTime to,
        long? roomId,
        [Range(1, 100)] int? limit,
        CancellationToken ct)
    {
        return _schedule.GetPageAsync(from, to, roomId, limit ?? 20, ct);
    }

    // решение: to > from проверяется здесь, до автоматического 400 от [ApiController], по сырым значениям
    // и только если обе даты разобрались, — чтобы все ошибки запроса пришли одним ответом.
    void IActionFilter.OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("from", out var from)
            && context.ActionArguments.TryGetValue("to", out var to)
            && to is DateTime end
            && from is DateTime start
            && end <= start)
        {
            ModelState.AddModelError("to", "The to field must be later than from.");
        }
    }

    void IActionFilter.OnActionExecuted(ActionExecutedContext context)
    {
    }
}
