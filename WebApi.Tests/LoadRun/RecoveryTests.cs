extern alias load;

using load::WebApi.LoadTests;

namespace WebApi.Tests.LoadRun;

[TestClass]
public class RecoveryTests
{
    [TestMethod]
    [DataRow(100.0, 100.0, true)]
    [DataRow(100.0, 150.0, true)]
    [DataRow(100.0, 150.001, false)]
    [DataRow(100.0, 10.0, true)]
    public void LatencyRecovered_OneAndHalfTimesBoundary(double before, double after, bool expected)
    {
        Assert.AreEqual(expected, Recovery.LatencyRecovered(before, after));
    }

    [TestMethod]
    public void EventsStopped_LastEventAtTenSeconds_True()
    {
        var started = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

        Assert.IsTrue(Recovery.EventsStopped(started.AddSeconds(10), started));
    }

    [TestMethod]
    public void EventsStopped_LastEventTickAfterTenSeconds_False()
    {
        var started = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

        Assert.IsFalse(Recovery.EventsStopped(started.AddSeconds(10).AddTicks(1), started));
    }

    [TestMethod]
    public void EventsStopped_NoEvents_True()
    {
        Assert.IsTrue(Recovery.EventsStopped(null, new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero)));
    }

    [TestMethod]
    [DataRow(50.0, 50.0)]
    [DataRow(95.0, 95.0)]
    [DataRow(99.0, 99.0)]
    [DataRow(100.0, 100.0)]
    public void Percentile_NearestRank(double percent, double expected)
    {
        var sorted = Enumerable.Range(1, 100).Select(x => (double)x).ToList();

        Assert.AreEqual(expected, Stats.Percentile(sorted, percent));
    }

    [TestMethod]
    public void Percentile_SingleValue_ThatValue()
    {
        Assert.AreEqual(42.0, Stats.Percentile([42.0], 99));
    }
}
