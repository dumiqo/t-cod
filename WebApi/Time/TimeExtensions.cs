namespace WebApi.Time;

public static class TimeExtensions
{
    // решение: время усекаем до микросекунды — точности timestamptz в Postgres, иначе одна и та же
    // бронь отдаёт разное createdAt из ответа и из базы.
    public static DateTime TruncateToMicroseconds(this DateTime value)
    {
        return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, value.Kind);
    }
}
