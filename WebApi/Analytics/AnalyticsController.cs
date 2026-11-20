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
    [HttpGet("load")]
    [Authorize(Roles = AdminRole)]
    public RoomLoadResponse Load(
        [ModelBinder(typeof(IsoDateModelBinder))] DateOnly from,
        [ModelBinder(typeof(IsoDateModelBinder))] DateOnly to,
        long? roomId)
    {
        return new RoomLoadResponse([]);
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
