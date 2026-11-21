using System.Globalization;
using System.Text.RegularExpressions;

namespace WebApi.Binding;

public static partial class UtcDateTimeParser
{
    private static readonly TimeSpan MaxOffset = TimeSpan.FromHours(14);

    public static bool TryParse(string? text, out DateTime value)
    {
        value = default;

        if (text is null)
        {
            return false;
        }

        var match = Pattern().Match(text);

        if (!match.Success || !IsoDateParser.TryParse(match.Groups["date"].Value, out var date))
        {
            return false;
        }

        var hour = Number(match.Groups["hour"], 0);
        var minute = Number(match.Groups["minute"], 0);
        var second = Number(match.Groups["second"], 0);

        if (hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        var fraction = match.Groups["fraction"].Value.PadRight(7, '0');
        var local = date.ToDateTime(new TimeOnly(hour, minute, second), DateTimeKind.Utc)
            .AddTicks(int.Parse(fraction, CultureInfo.InvariantCulture));

        if (!TryParseOffset(match.Groups["offset"].Value, out var offset))
        {
            return false;
        }

        var ticks = local.Ticks - offset.Ticks;

        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        value = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    // решение: значение без смещения и голая дата — UTC (голая дата — полночь): контракт не запрещает дату без времени.
    private static bool TryParseOffset(string text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;

        if (text is "" or "Z")
        {
            return true;
        }

        var hours = int.Parse(text.AsSpan(1, 2), CultureInfo.InvariantCulture);
        var minutes = int.Parse(text.AsSpan(4, 2), CultureInfo.InvariantCulture);

        if (minutes > 59)
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, 0);

        if (offset > MaxOffset)
        {
            return false;
        }

        if (text[0] == '-')
        {
            offset = offset.Negate();
        }

        return true;
    }

    private static int Number(Group group, int fallback)
    {
        return group.Success ? int.Parse(group.Value, CultureInfo.InvariantCulture) : fallback;
    }

    // решение: пробел на месте знака смещения — это «+», который клиент не закодировал в query (`+` декодируется в пробел);
    // принимается только там, где иначе было бы 400, — между временем и ±HH:mm.
    [GeneratedRegex(
        @"^(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})(?:T(?<hour>[0-9]{2}):(?<minute>[0-9]{2})(?::(?<second>[0-9]{2})(?:\.(?<fraction>[0-9]{1,7}))?)?(?<offset>Z|[+ -][0-9]{2}:[0-9]{2})?)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
