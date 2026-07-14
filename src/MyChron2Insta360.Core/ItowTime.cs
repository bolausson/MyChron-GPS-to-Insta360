namespace MyChron2Insta360.Core;

/// <summary>
/// Converts a GPS time-of-week (iTOW, milliseconds) into absolute UTC. iTOW alone doesn't say which
/// week it is, so a rough calendar date (±3 days is plenty) selects the GPS week; iTOW then fixes the
/// instant to the millisecond. GPS time runs ahead of UTC by a fixed number of leap seconds.
/// </summary>
public static class ItowTime
{
    private static readonly DateTime GpsEpoch = new(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc);
    private const double SecondsPerWeek = 604800.0;

    /// <summary>GPS − UTC leap seconds. 18 since 2017; none announced through 2026.</summary>
    public const int DefaultLeapSeconds = 18;

    public static DateTime ToUtc(double itowMs, DateTime dateHint, int leapSeconds = DefaultLeapSeconds)
    {
        DateTime hintUtc = dateHint.Kind == DateTimeKind.Utc ? dateHint : DateTime.SpecifyKind(dateHint, DateTimeKind.Utc);
        double approxGpsSeconds = (hintUtc - GpsEpoch).TotalSeconds + leapSeconds;
        long week = (long)Math.Floor(approxGpsSeconds / SecondsPerWeek);
        double gpsSeconds = week * SecondsPerWeek + itowMs / 1000.0;
        return GpsEpoch.AddSeconds(gpsSeconds - leapSeconds);
    }
}
