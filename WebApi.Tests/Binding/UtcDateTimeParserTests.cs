using System.Globalization;
using WebApi.Binding;

namespace WebApi.Tests.Binding;

[TestClass]
public class UtcDateTimeParserTests
{
    [TestMethod]
    [DataRow("2026-09-22", "2026-09-22T00:00:00.0000000Z")]
    [DataRow("2026-09-22T10:00", "2026-09-22T10:00:00.0000000Z")]
    [DataRow("2026-09-22T10:00:30", "2026-09-22T10:00:30.0000000Z")]
    [DataRow("2026-09-22T10:00:30.5", "2026-09-22T10:00:30.5000000Z")]
    [DataRow("2026-09-22T10:00:30.123", "2026-09-22T10:00:30.1230000Z")]
    [DataRow("2026-09-22T10:00:30.1234567", "2026-09-22T10:00:30.1234567Z")]
    [DataRow("2026-09-22T10:00Z", "2026-09-22T10:00:00.0000000Z")]
    [DataRow("2026-09-22T10:00:00.123Z", "2026-09-22T10:00:00.1230000Z")]
    [DataRow("2026-09-22T13:00+03:00", "2026-09-22T10:00:00.0000000Z")]
    [DataRow("2026-09-22T05:00-05:00", "2026-09-22T10:00:00.0000000Z")]
    [DataRow("2026-09-22T10:00+00:00", "2026-09-22T10:00:00.0000000Z")]
    [DataRow("2026-09-22T10:00:00+05:30", "2026-09-22T04:30:00.0000000Z")]
    [DataRow("2026-09-22T13:00:30.123+03:00", "2026-09-22T10:00:30.1230000Z")]
    [DataRow("2026-09-22T10:00-00:00", "2026-09-22T10:00:00.0000000Z")]
    [DataRow("2026-01-01T02:00+03:00", "2025-12-31T23:00:00.0000000Z")]
    [DataRow("0001-01-01T00:00-01:00", "0001-01-01T01:00:00.0000000Z")]
    [DataRow("2026-09-22T01:00+14:00", "2026-09-21T11:00:00.0000000Z")]
    [DataRow("2026-09-22T23:30-14:00", "2026-09-23T13:30:00.0000000Z")]
    [DataRow("2026-09-22T13:00 03:00", "2026-09-22T10:00:00.0000000Z")]
    [DataRow("2026-09-22T13:00:30.123 03:00", "2026-09-22T10:00:30.1230000Z")]
    [DataRow("2026-09-22T10:00 00:00", "2026-09-22T10:00:00.0000000Z")]
    [DataRow("2026-09-22T01:00 14:00", "2026-09-21T11:00:00.0000000Z")]
    [DataRow("2026-01-01T02:00 03:00", "2025-12-31T23:00:00.0000000Z")]
    [DataRow("2028-02-29T00:00", "2028-02-29T00:00:00.0000000Z")]
    [DataRow("0001-01-01T00:00", "0001-01-01T00:00:00.0000000Z")]
    [DataRow("9999-12-31T23:59:59.9999999Z", "9999-12-31T23:59:59.9999999Z")]
    public void TryParse_Accepted_ReturnsUtcMoment(string text, string expected)
    {
        var parsed = UtcDateTimeParser.TryParse(text, out var value);

        Assert.IsTrue(parsed, text);
        Assert.AreEqual(DateTimeKind.Utc, value.Kind);
        Assert.AreEqual(expected, value.ToString("O", CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("2026-09-22 10:00")]
    [DataRow("2026-09-22t10:00")]
    [DataRow("2026-09-22T10:00z")]
    [DataRow("2026-09-22Z")]
    [DataRow("2026-09-22T")]
    [DataRow("2026-09-22T10")]
    [DataRow("2026-09-22T10:00:00.123+0300")]
    [DataRow("2026-09-22+03:00")]
    [DataRow("1900-02-29T10:00")]
    [DataRow("2026-09-22T1:00")]
    [DataRow("2026-09-22T10:00:5")]
    [DataRow("2026-09-22T10:00:00.")]
    [DataRow("2026-09-22T10:00:00,5")]
    [DataRow("2026-09-22T10:00:00.12345678")]
    [DataRow("2026-09-22T10:00:60")]
    [DataRow("2026-09-22T10:60")]
    [DataRow("2026-09-22T24:00")]
    [DataRow("2026-02-30T10:00")]
    [DataRow("2026-13-01T10:00")]
    [DataRow("0000-01-01T10:00")]
    [DataRow("2026-9-22T10:00")]
    [DataRow("26-09-22T10:00")]
    [DataRow("+2026-09-22T10:00")]
    [DataRow("2026-09-22T10:00+0300")]
    [DataRow("2026-09-22T10:00+03")]
    [DataRow("2026-09-22T10:00+03:60")]
    [DataRow("2026-09-22T10:00+14:01")]
    [DataRow("2026-09-22T10:00-15:00")]
    [DataRow("0001-01-01T00:00+01:00")]
    [DataRow("9999-12-31T23:59-01:00")]
    [DataRow(" 2026-09-22T10:00")]
    [DataRow("2026-09-22T10:00 ")]
    [DataRow("2026-09-22T10:00\n")]
    [DataRow("２０２６-09-22T10:00")]
    [DataRow("2026-09-22T10:00:00.123+03:00Z")]
    [DataRow("2026-09-22T10:00 0300")]
    [DataRow("2026-09-22T10:00 03")]
    [DataRow("2026-09-22T10:00 03:60")]
    [DataRow("2026-09-22T10:00 14:01")]
    [DataRow("2026-09-22T10:00  03:00")]
    [DataRow("2026-09-22T10:00 Z")]
    [DataRow("2026-09-22T10:00\t03:00")]
    [DataRow("2026-09-22 03:00")]
    [DataRow("2026-09-22 10:00 03:00")]
    [DataRow("9999-12-31T23:59 -01:00")]
    [DataRow("0001-01-01T00:00 01:00")]
    public void TryParse_Rejected_ReturnsFalse(string? text)
    {
        Assert.IsFalse(UtcDateTimeParser.TryParse(text, out _), text);
    }
}
