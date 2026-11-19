using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebApi.Binding;
using WebApi.Controllers;

namespace WebApi.Tests.Contract;

[ApiController]
[Route("probe")]
public class ContractProbeController : ClubApiController
{
    public const string Operation = "probe-operation";

    public const string Reason = "probe-reason";

    [HttpPost("reject")]
    public IActionResult RejectRequest()
    {
        return Reject(Operation, Reason);
    }

    [HttpGet("items/{id}")]
    public ActionResult<long> GetItem(long id)
    {
        return id;
    }

    [HttpPost("body")]
    public ActionResult<int> PostBody([FromBody, Required] ProbeBody? body)
    {
        return body!.Seats!.Value;
    }

    [HttpGet("moment")]
    public ActionResult<DateTime> GetMoment([ModelBinder(typeof(UtcDateTimeModelBinder))] DateTime at)
    {
        return at;
    }

    [HttpGet("period")]
    public ActionResult<ProbePeriod> GetPeriod(
        [ModelBinder(typeof(IsoDateModelBinder))] DateOnly from,
        [ModelBinder(typeof(IsoDateModelBinder))] DateOnly to)
    {
        return new ProbePeriod(from, to);
    }

    public sealed record ProbePeriod(DateOnly From, DateOnly To);

    public sealed class ProbeBody
    {
        [Required]
        public int? Seats { get; init; }
    }
}
