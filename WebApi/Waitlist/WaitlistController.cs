using System.ComponentModel.DataAnnotations;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.Mvc;
using WebApi.Controllers;
using WebApi.Enrollments;

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
        var result = await _waitlist.JoinAsync(CurrentUserId, request!.ClassId!.Value, request.Seats!.Value, idempotencyKey!, ct);

        return result switch
        {
            JoinWaitlistResult.Created created =>
                StatusCode(StatusCodes.Status201Created, JoinWaitlistResponse.From(created.Entry, created.Position)),
            JoinWaitlistResult.Replayed replayed => StatusCode(StatusCodes.Status201Created, replayed.Response),
            JoinWaitlistResult.ClassNotFound => NotFound(),
            JoinWaitlistResult.Rejected rejected => Reject(Operations.JoinWaitlist, rejected.Reason),
            _ => throw new InvalidOperationException($"Unknown join waitlist result {result}.")
        };
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Leave(long id, CancellationToken ct)
    {
        var result = await _waitlist.LeaveAsync(CurrentUserId, id, ct);

        return result switch
        {
            LeaveWaitlistResult.Left left => Ok(new LeaveWaitlistResponse(left.Id, WaitlistStatuses.Cancelled)),
            LeaveWaitlistResult.NotFound => NotFound(),
            LeaveWaitlistResult.Rejected rejected => Reject(Operations.LeaveWaitlist, rejected.Reason),
            _ => throw new InvalidOperationException($"Unknown leave waitlist result {result}.")
        };
    }
}
