using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Observability;
using WebApi.RateLimiting;

namespace WebApi.Controllers;

[Authorize]
[RateLimited]
public abstract class ClubApiController : BaseApiController
{
    protected ObjectResult Reject(string operation, string reason)
    {
        var services = HttpContext.RequestServices;

        Log.RequestRejected(services.GetRequiredService<ILogger<ClubApiController>>(), operation, reason, CurrentUserId);
        services.GetRequiredService<ClubMetrics>().RequestRejected(operation, reason);

        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, StatusCodes.Status409Conflict);
        problem.Extensions["reason"] = reason;

        return new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict };
    }
}
