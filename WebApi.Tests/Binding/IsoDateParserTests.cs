using WebApi.Binding;

namespace WebApi.Tests.Binding;

[TestClass]
public class IsoDateParserTests
{
    [TestMethod]
    [DataRow("2026-09-22", 2026, 9, 22)]
    [DataRow("2028-02-29", 2028, 2, 29)]
    [DataRow("0001-01-01", 1, 1, 1)]
    [DataRow("9999-12-31", 9999, 12, 31)]
    public void TryParse_Accepted_ReturnsDate(string text, int year, int month, int day)
    {
        var parsed = IsoDateParser.TryParse(text, out var value);

        Assert.IsTrue(parsed, text);
        Assert.AreEqual(new DateOnly(year, month, day), value);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("2026-09-22T10:00")]
    [DataRow("2026-09-22T00:00:00Z")]
    [DataRow("2026-09-22Z")]
    [DataRow("2026-9-22")]
    [DataRow("2026-09-2")]
    [DataRow("26-09-22")]
    [DataRow("2026/09/22")]
    [DataRow("20260922")]
    [DataRow("2026-02-29")]
    [DataRow("2026-13-01")]
    [DataRow("0000-01-01")]
    [DataRow(" 2026-09-22")]
    [DataRow("2026-09-22 ")]
    [DataRow("2026-09-22\n")]
    [DataRow("２０２６-09-22")]
    public void TryParse_Rejected_ReturnsFalse(string? text)
    {
        Assert.IsFalse(IsoDateParser.TryParse(text, out _), text);
    }
}
