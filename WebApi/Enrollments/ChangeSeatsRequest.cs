using System.ComponentModel.DataAnnotations;

namespace WebApi.Enrollments;

public sealed class ChangeSeatsRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; init; }
}
