namespace MyChron2Insta360.Core;

/// <summary>
/// Seconds added to a time, exact to the tick. DateTime.AddSeconds cuts the fraction of its argument to
/// whole ticks rather than rounding it, and a value such as 1234.56 s is held as 1234.5599999…, so
/// about every other result came out 100 ns early: a millisecond early once written to the
/// millisecond, as the GPX and the IMU CSV are.
/// </summary>
public static class Clock
{
    public static DateTime PlusSeconds(this DateTime t, double seconds)
    {
        if (!double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "not a finite number of seconds");
        return t.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    }
}
