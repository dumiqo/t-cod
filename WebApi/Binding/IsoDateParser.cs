using System.Globalization;
using System.Text.RegularExpressions;

namespace WebApi.Binding;

public static partial class IsoDateParser
{
    public static bool TryParse(string? text, out DateOnly value)
    {
        value = default;

        return text is not null
            && Pattern().IsMatch(text)
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
