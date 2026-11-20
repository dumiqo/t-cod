using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using DataAccessLayer.Repositories;

namespace WebApi.Schedule;

public sealed record ContinuationToken(DateTime From, DateTime To, long? RoomId, ScheduleCursor After)
{
    private const byte Version = 1;
    private const int LengthWithoutRoom = 34;
    private const int LengthWithRoom = 42;

    public string Encode()
    {
        var bytes = new byte[RoomId is null ? LengthWithoutRoom : LengthWithRoom];
        bytes[0] = Version;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1), From.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(9), To.Ticks);
        var offset = 18;

        if (RoomId is { } roomId)
        {
            bytes[17] = 1;
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), roomId);
            offset += 8;
        }

        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), After.StartsAt.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 8), After.Id);

        return Base64Url.EncodeToString(bytes);
    }

    public static bool TryDecode(string text, [NotNullWhen(true)] out ContinuationToken? token)
    {
        token = null;
        Span<byte> bytes = stackalloc byte[LengthWithRoom];

        if (text.Length == 0
            || !text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')
            || !Base64Url.TryDecodeFromChars(text, bytes, out var length)
            || length < LengthWithoutRoom
            || bytes[0] != Version
            || bytes[17] > 1
            || length != (bytes[17] == 1 ? LengthWithRoom : LengthWithoutRoom))
        {
            return false;
        }

        var offset = 18;
        long? roomId = null;

        if (bytes[17] == 1)
        {
            roomId = BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
            offset += 8;
        }

        if (!TryReadTime(bytes[1..], out var from)
            || !TryReadTime(bytes[9..], out var to)
            || !TryReadTime(bytes[offset..], out var startsAt))
        {
            return false;
        }

        token = new ContinuationToken(from, to, roomId, new ScheduleCursor(startsAt, BinaryPrimitives.ReadInt64LittleEndian(bytes[(offset + 8)..])));
        return true;
    }

    private static bool TryReadTime(ReadOnlySpan<byte> bytes, out DateTime value)
    {
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        var valid = ticks >= 0 && ticks <= DateTime.MaxValue.Ticks;
        value = valid ? new DateTime(ticks, DateTimeKind.Utc) : default;
        return valid;
    }
}
