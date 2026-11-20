using System.Diagnostics.Metrics;

namespace WebApi.Observability;

public sealed class ClubMetrics
{
    public const string MeterName = "FitnessClub.Api";

    private readonly Counter<long> _requestsRejected;
    private readonly Counter<long> _enrollmentsCreated;
    private readonly Counter<long> _visitsCharged;
    private readonly Counter<long> _idempotencyReplayed;
    private readonly Counter<long> _visitsRefunded;
    private readonly Counter<long> _waitlistJoined;
    private readonly Counter<long> _waitlistLeft;

    public ClubMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _requestsRejected = meter.CreateCounter<long>("club.requests.rejected");
        _enrollmentsCreated = meter.CreateCounter<long>("club.enrollments.created");
        _visitsCharged = meter.CreateCounter<long>("club.visits.charged");
        _idempotencyReplayed = meter.CreateCounter<long>("club.idempotency.replayed");
        _visitsRefunded = meter.CreateCounter<long>("club.visits.refunded");
        _waitlistJoined = meter.CreateCounter<long>("club.waitlist.joined");
        _waitlistLeft = meter.CreateCounter<long>("club.waitlist.left");
    }

    public void WaitlistJoined(int seats)
    {
        _waitlistJoined.Add(seats);
    }

    public void WaitlistLeft(int seats)
    {
        _waitlistLeft.Add(seats);
    }

    public void VisitsRefunded(int visits)
    {
        _visitsRefunded.Add(visits);
    }

    public void IdempotencyReplayed(string operation)
    {
        _idempotencyReplayed.Add(1, new KeyValuePair<string, object?>("operation", operation));
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
