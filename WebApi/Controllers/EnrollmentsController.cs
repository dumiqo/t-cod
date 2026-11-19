using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebApi.Enrollments;

namespace WebApi.Controllers;

[ApiController]
[Route("enrollments")]
public class EnrollmentsController : ClubApiController
{
    private readonly EnrollmentService _enrollments;

    public EnrollmentsController(EnrollmentService enrollments)
    {
        _enrollments = enrollments;
    }

    [HttpPost]
    public async Task<IActionResult> Enroll(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        [FromBody, Required] EnrollRequest? request,
        CancellationToken ct)
    {
        var result = await _enrollments.EnrollAsync(
            CurrentUserId,
            request!.ClassId!.Value,
            request.Seats!.Value,
            idempotencyKey!,
            ct);

        return result switch
        {
            EnrollResult.Created created => StatusCode(StatusCodes.Status201Created, EnrollmentResponse.From(created.Enrollment)),
            EnrollResult.Replayed replayed => StatusCode(StatusCodes.Status201Created, replayed.Response),
            EnrollResult.ClassNotFound => NotFound(),
            EnrollResult.Rejected rejected => Reject(Operations.Enroll, rejected.Reason),
            _ => throw new InvalidOperationException($"Unknown enroll result {result}.")
        };
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetCard(long id, CancellationToken ct)
    {
        var card = await _enrollments.GetCardAsync(CurrentUserId, id, ct);

        return card is null ? NotFound() : Ok(EnrollmentCardResponse.From(card));
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct)
    {
        var result = await _enrollments.CancelAsync(CurrentUserId, id, ct);

        return result switch
        {
            CancelResult.Cancelled cancelled => Ok(CancelEnrollmentResponse.From(cancelled)),
            CancelResult.NotFound => NotFound(),
            CancelResult.Rejected rejected => Reject(Operations.CancelEnrollment, rejected.Reason),
            _ => throw new InvalidOperationException($"Unknown cancel result {result}.")
        };
    }
}
