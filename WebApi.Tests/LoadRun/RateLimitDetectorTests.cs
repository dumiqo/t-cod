extern alias load;

using load::WebApi.LoadTests;

namespace WebApi.Tests.LoadRun;

[TestClass]
public class RateLimitDetectorTests
{
    private static List<Sample> Burst(long user, int count, long startMs = 0, int status = 200)
    {
        return Enumerable.Range(0, count).Select(i => new Sample("card", user, startMs + i * 100, startMs + i * 100 + 10, status)).ToList();
    }

    [TestMethod]
    public void Find_SixtyWithinSixtySeconds_NoFindings()
    {
        var findings = RateLimitDetector.Find(Burst(7, 60));

        Assert.AreEqual(0, findings.Count);
    }

    [TestMethod]
    [DataRow(59_999L, 1)]
    [DataRow(60_000L, 0)]
    [DataRow(60_001L, 0)]
    public void Find_SixtyFirstAroundWindowEnd_DefiniteOnlyInsideWindow(long sixtyFirstMs, int expected)
    {
        var samples = Enumerable.Range(0, 60).Select(i => new Sample("card", 7, i * 10, i * 10, 200)).ToList();
        samples.Add(new Sample("card", 7, sixtyFirstMs, sixtyFirstMs, 200));

        var findings = RateLimitDetector.Find(samples);

        Assert.AreEqual(expected, findings.Count);
        Assert.IsTrue(findings.All(x => x is { User: 7, StartMs: 0, Definite: true }));
    }

    [TestMethod]
    public void Find_ServerMomentUncertain_Borderline()
    {
        var samples = new List<Sample> { new("card", 7, 0, 100, 200) };
        samples.AddRange(Enumerable.Range(1, 59).Select(i => new Sample("card", 7, 100 + i * 10, 100 + i * 10, 200)));
        samples.Add(new Sample("card", 7, 59_950, 60_000, 200));

        var findings = RateLimitDetector.Find(samples);

        Assert.AreEqual(1, findings.Count);
        Assert.AreEqual(new LimitFinding(7, 0, false), findings[0]);
    }

    [TestMethod]
    public void Find_UnorderedInput_SortsByStart()
    {
        var samples = Burst(7, 61);
        samples.Reverse();

        var findings = RateLimitDetector.Find(samples);

        CollectionAssert.AreEqual(new[] { new LimitFinding(7, 0, true) }, findings);
    }

    [TestMethod]
    public void Find_TooManyRequestsResponses_NotCounted()
    {
        var samples = Burst(7, 60);
        samples.AddRange(Burst(7, 30, startMs: 6_000, status: 429));

        var findings = RateLimitDetector.Find(samples);

        Assert.AreEqual(0, findings.Count);
    }

    [TestMethod]
    public void Find_DifferentUsers_NotMixed()
    {
        var samples = Burst(7, 40);
        samples.AddRange(Burst(8, 40));

        var findings = RateLimitDetector.Find(samples);

        Assert.AreEqual(0, findings.Count);
    }

    [TestMethod]
    public void Find_SixtyTwoInWindow_OneFindingPerWindowStart()
    {
        var samples = Burst(7, 62);
        samples.AddRange(Burst(8, 61));

        var findings = RateLimitDetector.Find(samples);

        CollectionAssert.AreEquivalent(
            new[] { new LimitFinding(7, 0, true), new LimitFinding(7, 100, true), new LimitFinding(8, 0, true) },
            findings);
    }
}
