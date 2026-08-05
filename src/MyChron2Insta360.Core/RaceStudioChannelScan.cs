using System.Globalization;

namespace MyChron2Insta360.Core;

/// <summary>One per-channel file from a RaceStudio "_CSV" folder: its name, units and samples.</summary>
public sealed class ChannelSeries
{
    public string Name = "";                 // the file name, which is the channel name
    public string Units = "";                // second cell of the units row
    public string SourceFile = "";
    public List<double> Times { get; } = new();
    public List<double> Values { get; } = new();

    /// <summary>Every sample exactly zero: the signature of a sensor that is not connected.</summary>
    public bool AllZero => Values.Count > 0 && Values.All(v => v == 0.0);

    /// <summary>Flat but not zero. Can be perfectly legitimate, so it is kept and only warned about.</summary>
    public bool Constant => Values.Count > 1 && !AllZero && Values.Min() == Values.Max();
}

/// <summary>
/// Finds the per-channel CSVs in a RaceStudio export without assuming which channels exist. A MyChron
/// with probes fitted logs water temperature, EGT, lambda, brake pressure and arbitrary expansion
/// channels, so the list is discovered rather than hard-coded.
///
/// A channel file is recognised by its shape, not its name: a "time, value" header row followed by an
/// "s, unit" row. That admits any channel the logger invents and rejects the GPS, lap and merged files
/// (including this tool's own outputs) without needing to know their names.
/// </summary>
public static class RaceStudioChannelScan
{
    /// <summary>Files that are handled elsewhere, or are our own output.</summary>
    private static bool IsHandledElsewhere(string fileName) =>
        fileName.StartsWith('_') ||                                             // _GPS_o, _GPS, _laps_and_splits
        fileName.EndsWith("_imu.csv", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith("_TelemetryOverlay.csv", StringComparison.OrdinalIgnoreCase);

    /// <summary>The six IMU channels, which come from the tilt-corrected sample stream instead.</summary>
    public static bool IsImuChannel(string name) =>
        name.StartsWith("Accelerometer", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Gyro", StringComparison.OrdinalIgnoreCase);

    public static List<ChannelSeries> Discover(string folder)
    {
        var found = new List<ChannelSeries>();
        if (!Directory.Exists(folder)) return found;

        foreach (var path in Directory.EnumerateFiles(folder, "*.csv").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (IsHandledElsewhere(Path.GetFileName(path))) continue;
            if (Read(path) is ChannelSeries s) found.Add(s);
        }
        return found;
    }

    /// <summary>Read one per-channel file, or null when it is not shaped like one.</summary>
    public static ChannelSeries? Read(string path)
    {
        using var reader = new StreamReader(path);
        if (reader.ReadLine() is not string head || reader.ReadLine() is not string unitsRow) return null;

        var h = head.Split(',');
        var u = unitsRow.Split(',');
        if (h.Length != 2 || u.Length != 2) return null;
        if (!h[0].Trim().Equals("time", StringComparison.OrdinalIgnoreCase)) return null;
        if (!u[0].Trim().Equals("s", StringComparison.OrdinalIgnoreCase)) return null;

        var series = new ChannelSeries
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Units = u[1].Trim(),
            SourceFile = path,
        };

        while (reader.ReadLine() is string line)
        {
            int c = line.IndexOf(',');
            if (c <= 0) continue;
            if (!double.TryParse(line.AsSpan(0, c), NumberStyles.Any, CultureInfo.InvariantCulture, out double t)) continue;
            if (!double.TryParse(line.AsSpan(c + 1).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v)) continue;
            series.Times.Add(t);
            series.Values.Add(v);
        }
        return series.Values.Count > 0 ? series : null;
    }
}
