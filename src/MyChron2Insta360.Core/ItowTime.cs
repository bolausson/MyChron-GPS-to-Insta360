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
    private const long TicksPerWeek = 604800L * TimeSpan.TicksPerSecond;

    /// <summary>GPS - UTC leap seconds. 18 since 2017; none announced through 2026.</summary>
    public const int DefaultLeapSeconds = 18;

    /// <summary>
    /// Exact UTC from the GPS week number and the time of week, with no calendar hint needed. The xrk
    /// files carry the week number, so this is preferred whenever it is available.
    /// </summary>
    /// <remarks>
    /// Counted in ticks. As one double, the seconds since 1980 run to ten digits, which leaves steps of
    /// about 0.24 µs, coarser than a tick; cut to the tick below, about every other fix was then written
    /// a millisecond early.
    /// </remarks>
    public static DateTime FromWeek(long weekNumber, double itowMs, int leapSeconds = DefaultLeapSeconds) =>
        GpsEpoch.AddTicks(weekNumber * TicksPerWeek - leapSeconds * TimeSpan.TicksPerSecond)
                .AddTicks((long)Math.Round(itowMs * TimeSpan.TicksPerMillisecond));

    public static DateTime ToUtc(double itowMs, DateTime dateHint, int leapSeconds = DefaultLeapSeconds)
    {
        DateTime hintUtc = dateHint.Kind == DateTimeKind.Utc ? dateHint : DateTime.SpecifyKind(dateHint, DateTimeKind.Utc);
        double approxGpsSeconds = (hintUtc - GpsEpoch).TotalSeconds + leapSeconds;
        // The week that puts the fix nearest the hint, which may then be off by up to three and a half
        // days. The week the hint itself fell in was a whole week wrong when the two straddled the change
        // of GPS week at midnight from Saturday to Sunday: a folder dated by local time does that for a
        // Saturday evening session in the Americas, or a Sunday morning one in Asia or Australia.
        long week = (long)Math.Round((approxGpsSeconds - itowMs / 1000.0) / SecondsPerWeek);
        return FromWeek(week, itowMs, leapSeconds);
    }

    /// <summary>
    /// UTC from iTOW, in the last GPS week that does not put the fix after <paramref name="latestUtc"/>:
    /// for a time known to come after the fix, such as when its file was written, days later at times.
    /// Right for anything written less than a week after the fix. A minute of slack allows for the
    /// computer's clock; an hour would already have cost a real export written 6 days 23 h 58 min on.
    /// </summary>
    public static DateTime ToUtcNotAfter(double itowMs, DateTime latestUtc, int leapSeconds = DefaultLeapSeconds)
    {
        DateTime latest = latestUtc.Kind == DateTimeKind.Utc ? latestUtc : DateTime.SpecifyKind(latestUtc, DateTimeKind.Utc);
        double latestGpsSeconds = (latest - GpsEpoch).TotalSeconds + leapSeconds + 60;
        long week = (long)Math.Floor((latestGpsSeconds - itowMs / 1000.0) / SecondsPerWeek);
        return FromWeek(week, itowMs, leapSeconds);
    }
}
