using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebApi.Controllers;

namespace WebApi.Waitlist;

[ApiController]
[Route("enrollments/waitlist")]
public class WaitlistController : ClubApiController
{
    private readonly WaitlistService _waitlist;

    public WaitlistController(WaitlistService waitlist)
    {
        _waitlist = waitlist;
    }

    [HttpPost]
    public async Task<IActionResult> Join(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        [FromBody, Required] JoinWaitlistRequest? request,
        CancellationToken ct)
    {
        var result = await _waitlist.JoinAsync(CurrentUserId, request!.ClassId!.Value, request.Seats!.Value, ct);

        return result switch
        {
            JoinWaitlistResult.Created created =>
                StatusCode(StatusCodes.Status201Created, JoinWaitlistResponse.From(created.Entry, created.Position)),
            JoinWaitlistResult.ClassNotFound => NotFound(),
            _ => throw new InvalidOperationException($"Unknown join waitlist result {result}.")
        };
    }
}
