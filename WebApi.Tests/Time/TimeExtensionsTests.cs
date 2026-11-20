using WebApi.Time;

namespace WebApi.Tests.Time;

[TestClass]
public class TimeExtensionsTests
{
    [TestMethod]
    [DataRow(1234567L, 1234560L)]
    [DataRow(9999999L, 9999990L)]
    [DataRow(1L, 0L)]
    [DataRow(9L, 0L)]
    [DataRow(10L, 10L)]
    [DataRow(0L, 0L)]
    public void TruncateToMicroseconds_DropsTicksBelowMicrosecond(long ticks, long expected)
    {
        var second = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);

        var truncated = second.AddTicks(ticks).TruncateToMicroseconds();

        Assert.AreEqual(second.AddTicks(expected), truncated);
    }

    [TestMethod]
    public void TruncateToMicroseconds_KeepsUtcKind()
    {
        var value = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc).AddTicks(7);

        Assert.AreEqual(DateTimeKind.Utc, value.TruncateToMicroseconds().Kind);
    }

    [TestMethod]
    [DataRow(1L, 10L)]
    [DataRow(9L, 10L)]
    [DataRow(10L, 10L)]
    [DataRow(11L, 20L)]
    [DataRow(0L, 0L)]
    [DataRow(9999991L, 10000000L)]
    public void CeilingToMicroseconds_RoundsTicksUpToMicrosecond(long ticks, long expected)
    {
        var second = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);

        var rounded = second.AddTicks(ticks).CeilingToMicroseconds();

        Assert.AreEqual(second.AddTicks(expected), rounded);
        Assert.AreEqual(DateTimeKind.Utc, rounded.Kind);
    }

    [TestMethod]
    public void CeilingToMicroseconds_MaxValue_StaysMaxValue()
    {
        Assert.AreEqual(DateTime.MaxValue, DateTime.MaxValue.CeilingToMicroseconds());
    }

    [TestMethod]
    public void TruncateToMicroseconds_MaxValue_DoesNotOverflow()
    {
        Assert.AreEqual(
            DateTime.MaxValue.AddTicks(-9),
            DateTime.MaxValue.TruncateToMicroseconds());
    }
}
