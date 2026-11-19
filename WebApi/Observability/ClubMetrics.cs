using System.Diagnostics.Metrics;

namespace WebApi.Observability;

public sealed class ClubMetrics
{
    public const string MeterName = "FitnessClub.Api";

    private readonly Counter<long> _requestsRejected;
    private readonly Counter<long> _enrollmentsCreated;
    private readonly Counter<long> _visitsCharged;

    public ClubMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _requestsRejected = meter.CreateCounter<long>("club.requests.rejected");
        _enrollmentsCreated = meter.CreateCounter<long>("club.enrollments.created");
        _visitsCharged = meter.CreateCounter<long>("club.visits.charged");
    }

    public void EnrollmentCreated(int seats)
    {
        _enrollmentsCreated.Add(seats);
    }

    public void VisitsCharged(int visits)
    {
        _visitsCharged.Add(visits);
    }

    public void RequestRejected(string operation, string reason)
    {
        _requestsRejected.Add(
            1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("reason", reason));
    }
}
