using System.Diagnostics.Metrics;

namespace WebApi.Observability;

public sealed class ClubMetrics
{
    public const string MeterName = "FitnessClub.Api";

    private readonly Counter<long> _requestsRejected;

    public ClubMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _requestsRejected = meter.CreateCounter<long>("club.requests.rejected");
    }

    public void RequestRejected(string operation, string reason)
    {
        _requestsRejected.Add(
            1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("reason", reason));
    }
}
