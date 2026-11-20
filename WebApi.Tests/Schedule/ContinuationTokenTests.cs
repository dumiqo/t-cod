using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text.RegularExpressions;
using DataAccessLayer.Repositories;
using WebApi.Schedule;

namespace WebApi.Tests.Schedule;

[TestClass]
public class ContinuationTokenTests
{
    private static readonly DateTime From = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2027, 1, 8, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StartsAt = new(2027, 1, 3, 10, 0, 0, 123, 456, DateTimeKind.Utc);

    [TestMethod]
    [DataRow(null)]
    [DataRow(5L)]
    [DataRow(long.MaxValue)]
    public void TryDecode_EncodedToken_ReturnsSameToken(long? roomId)
    {
        var token = new ContinuationToken(From, To, roomId, new ScheduleCursor(StartsAt, 42));

        var decoded = ContinuationToken.TryDecode(token.Encode(), out var actual);

        Assert.IsTrue(decoded);
        Assert.AreEqual(token, actual);
        Assert.AreEqual(DateTimeKind.Utc, actual!.From.Kind);
        Assert.AreEqual(DateTimeKind.Utc, actual.To.Kind);
        Assert.AreEqual(DateTimeKind.Utc, actual.After.StartsAt.Kind);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(-1L)]
    public void Encode_BytesThatNeedPaddingAndSpecialChars_UsesOnlyBase64UrlAlphabet(long? roomId)
    {
        var token = new ContinuationToken(DateTime.MaxValue, DateTime.MaxValue, roomId, new ScheduleCursor(DateTime.MaxValue, long.MaxValue));

        var text = token.Encode();

        StringAssert.Matches(text, new Regex("^[A-Za-z0-9_-]+$"));
        Assert.AreEqual(roomId is null ? 46 : 56, text.Length);
    }

    [TestMethod]
    public void TryDecode_HandBuiltToken_ReadsFields()
    {
        var text = Build(1, From.Ticks, To.Ticks, 1, 7, StartsAt.Ticks, 42);

        Assert.IsTrue(ContinuationToken.TryDecode(text, out var token));
        Assert.AreEqual(new ContinuationToken(From, To, 7, new ScheduleCursor(StartsAt, 42)), token);
    }

    [TestMethod]
    [DataRow("+")]
    [DataRow("/")]
    [DataRow("=")]
    [DataRow(" ")]
    [DataRow(".")]
    public void TryDecode_CharOutsideBase64UrlAlphabet_ReturnsFalse(string bad)
    {
        var valid = Build(1, From.Ticks, To.Ticks, 0, null, StartsAt.Ticks, 42);

        Assert.IsTrue(ContinuationToken.TryDecode(valid, out _));
        Assert.IsFalse(ContinuationToken.TryDecode(valid[..^1] + bad, out _));
        Assert.IsFalse(ContinuationToken.TryDecode(bad + valid[1..], out _));
        Assert.IsFalse(ContinuationToken.TryDecode(valid[..20] + bad + valid[20..], out _));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void TryDecode_EmptyOrWhitespace_ReturnsFalse(string text)
    {
        Assert.IsFalse(ContinuationToken.TryDecode(text, out _));
    }

    [TestMethod]
    public void TryDecode_StandardBase64Padding_ReturnsFalse()
    {
        var valid = Build(1, From.Ticks, To.Ticks, 0, null, StartsAt.Ticks, 42);

        Assert.IsFalse(ContinuationToken.TryDecode(valid + "==", out _));
    }

    [TestMethod]
    [DataRow(0, 33)]
    [DataRow(0, 35)]
    [DataRow(0, 42)]
    [DataRow(1, 34)]
    [DataRow(1, 41)]
    [DataRow(1, 43)]
    public void TryDecode_LengthNotMatchingRoomFlag_ReturnsFalse(int flag, int length)
    {
        var bytes = new byte[length];
        bytes[0] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1), From.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(9), To.Ticks);
        bytes[17] = (byte)flag;

        Assert.IsFalse(ContinuationToken.TryDecode(Base64Url.EncodeToString(bytes), out _));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(255)]
    public void TryDecode_VersionNotOne_ReturnsFalse(int version)
    {
        Assert.IsFalse(ContinuationToken.TryDecode(Build((byte)version, From.Ticks, To.Ticks, 0, null, StartsAt.Ticks, 42), out _));
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(255)]
    public void TryDecode_RoomFlagNotZeroOrOne_ReturnsFalse(int flag)
    {
        Assert.IsFalse(ContinuationToken.TryDecode(Build(1, From.Ticks, To.Ticks, (byte)flag, null, StartsAt.Ticks, 42), out _));
        Assert.IsFalse(ContinuationToken.TryDecode(Build(1, From.Ticks, To.Ticks, (byte)flag, 7, StartsAt.Ticks, 42), out _));
    }

    [TestMethod]
    [DataRow(0, -1L)]
    [DataRow(1, -1L)]
    [DataRow(2, -1L)]
    [DataRow(0, 3155378976000000000L)]
    [DataRow(1, 3155378976000000000L)]
    [DataRow(2, 3155378976000000000L)]
    [DataRow(2, long.MinValue)]
    public void TryDecode_TicksOutsideDateTime_ReturnsFalse(int field, long ticks)
    {
        var valid = new[] { From.Ticks, To.Ticks, StartsAt.Ticks };
        valid[field] = ticks;

        Assert.AreEqual(DateTime.MaxValue.Ticks + 1, 3155378976000000000L);
        Assert.IsFalse(ContinuationToken.TryDecode(Build(1, valid[0], valid[1], 0, null, valid[2], 42), out _));
    }

    [TestMethod]
    public void TryDecode_TicksAtDateTimeLimits_ReturnsTrue()
    {
        var text = Build(1, 0, DateTime.MaxValue.Ticks, 0, null, DateTime.MaxValue.Ticks, 0);

        Assert.IsTrue(ContinuationToken.TryDecode(text, out var token));
        Assert.AreEqual(
            new ContinuationToken(DateTime.MinValue, DateTime.MaxValue, null, new ScheduleCursor(DateTime.MaxValue, 0)),
            token);
    }

    private static string Build(byte version, long from, long to, byte flag, long? roomId, long startsAt, long id)
    {
        var bytes = new byte[roomId is null ? 34 : 42];
        bytes[0] = version;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1), from);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(9), to);
        bytes[17] = flag;
        var offset = 18;

        if (roomId is { } room)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), room);
            offset += 8;
        }

        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), startsAt);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 8), id);

        return Base64Url.EncodeToString(bytes);
    }
}
