using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Controllers;

namespace WebApi.Tests.Infrastructure;

[ApiController]
[Authorize]
[Route("harness")]
public class HarnessController : BaseApiController
{
    [HttpGet("me")]
    public ActionResult<long> Me()
    {
        return CurrentUserId;
    }
}
