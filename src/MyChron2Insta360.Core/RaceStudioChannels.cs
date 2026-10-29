using System.Globalization;

namespace MyChron2Insta360.Core;

/// <summary>One lap from the logger's lap/split table.</summary>
public sealed class LapSplit
{
    public int Number;
    public DateTime StartUtc;
    public double DurationS;
}

/// <summary>
/// Reads the non-GPS, non-IMU channels of a RaceStudio "_CSV" folder that map onto native FIT fields:
/// lap splits (_laps_and_splits.csv) and logger temperature. Both are optional.
/// </summary>
public static class RaceStudioChannels
{
    /// <summary>Lap splits, converted to absolute UTC using the session start.</summary>
    public static List<LapSplit> LoadLaps(string folder, DateTime? sessionStartUtc)
    {
        var laps = new List<LapSplit>();
        if (sessionStartUtc is not DateTime start) return laps;
        foreach (var (number, startS, durationS) in LoadLapsElapsed(folder))
            laps.Add(new LapSplit { Number = number, StartUtc = start.PlusSeconds(startS), DurationS = durationS });
        return laps;
    }

    /// <summary>
    /// Lap splits on the session's own elapsed-seconds base, which is what the Telemetry Overlay
    /// beacon markers need. Works without a GPS clock.
    /// </summary>
    public static List<(int Number, double StartS, double DurationS)> LoadLapsElapsed(string folder)
    {
        var laps = new List<(int, double, double)>();
        string path = Path.Combine(folder, "_laps_and_splits.csv");
        if (!File.Exists(path)) return laps;

        int n = 0;
        foreach (var line in File.ReadLines(path))
        {
            var f = line.Split(',');
            if (f.Length < 5) continue;
            // run, lap, split, start, time  (numeric rows only; skips the header and the units row)
            if (!double.TryParse(f[3].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double startS)) continue;
            if (!double.TryParse(f[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double durS)) continue;
            if (durS <= 0) continue;
            laps.Add((++n, startS, durS));
        }
        return laps;
    }

    /// <summary>Logger temperature samples (UTC, degrees C).</summary>
    public static List<(DateTime Utc, double C)> LoadTemperature(string folder, DateTime? sessionStartUtc)
    {
        var list = new List<(DateTime, double)>();
        string path = Path.Combine(folder, "Logger Temperature.csv");
        if (!File.Exists(path) || sessionStartUtc is not DateTime start) return list;

        foreach (var line in File.ReadLines(path))
        {
            int c = line.IndexOf(',');
            if (c <= 0) continue;
            if (!double.TryParse(line.AsSpan(0, c), NumberStyles.Any, CultureInfo.InvariantCulture, out double t)) continue;
            if (!double.TryParse(line.AsSpan(c + 1).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v)) continue;
            list.Add((start.PlusSeconds(t), v));
        }
        return list;
    }
}
