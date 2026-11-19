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
        var result = await _enrollments.EnrollAsync(CurrentUserId, request!.ClassId!.Value, request.Seats!.Value, ct);

        return result switch
        {
            EnrollResult.Created created => StatusCode(StatusCodes.Status201Created, EnrollmentResponse.From(created.Enrollment)),
            EnrollResult.ClassNotFound => NotFound(),
            EnrollResult.Rejected rejected => Reject(Operations.Enroll, rejected.Reason),
            _ => throw new InvalidOperationException($"Unknown enroll result {result}.")
        };
    }
}
