using System.ComponentModel.DataAnnotations;

namespace WebApi.Enrollments;

public sealed class EnrollRequest
{
    [Required]
    public long? ClassId { get; init; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; init; }
}
