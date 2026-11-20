using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Tests.Middleware;

[ApiController]
[AllowAnonymous]
[Route("probe/exceptions")]
public class ExceptionProbeController : ControllerBase
{
    public const string StartedMessage = "probe failure after response started";

    [HttpGet("aborted")]
    public async Task Aborted([FromServices] PipelineOutcome outcome)
    {
        outcome.Entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, HttpContext.RequestAborted);
    }

    [HttpGet("canceled")]
    public IActionResult Canceled()
    {
        throw new OperationCanceledException("probe cancellation");
    }

    [HttpGet("started")]
    public async Task Started()
    {
        Response.StatusCode = StatusCodes.Status200OK;
        await Response.StartAsync();
        throw new InvalidOperationException(StartedMessage);
    }
}
