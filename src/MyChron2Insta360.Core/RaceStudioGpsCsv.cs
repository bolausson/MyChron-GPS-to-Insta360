using System.Globalization;

namespace MyChron2Insta360.Core;

/// <summary>One GPS fix from a RaceStudio "Save as CSV" export (<c>_GPS_o.csv</c> / <c>_GPS.csv</c>).</summary>
public sealed class GpsSample
{
    public double TimeRel;    // seconds from session start
    public double ItowMs;     // GPS time-of-week in ms (absolute GPS time); NaN if absent
    public double Lat;        // decimal degrees
    public double Lon;        // decimal degrees
    public double Alt;        // metres
    public double SpeedMs;    // metres/second (RaceStudio GPS speed is already m/s)
    public double AccuracyM;  // reported position accuracy, metres
    public double HeadingDeg = double.NaN;   // course over ground, degrees true; NaN when not recorded
    public double Satellites = double.NaN;   // satellites in the fix; NaN when not recorded
}

public sealed class GpsSession
{
    public List<GpsSample> Samples { get; } = new();
    public string Name { get; set; } = "Session";
    public DateTime? DateHint { get; set; }   // rough date (UTC) used only to pick the GPS week for iTOW
    public int? GpsWeek { get; set; }         // exact GPS week when the source supplies it (xrk does)
    /// <summary>
    /// UTC at TimeRel 0, for a source that states UTC itself (a .vbo) rather than GPS time. It wins over
    /// iTOW, so neither a GPS week nor leap seconds come into it.
    /// </summary>
    public DateTime? StartUtc { get; set; }
    public string SourceFile { get; set; } = "";
    public double NativeHz { get; set; }
}

/// <summary>
/// Reads the per-channel GPS CSV(s) that RaceStudio3 writes on "Save as CSV". Columns are
/// <c>time, itow, lat, lon, alt, speed, accuracy</c>. <c>_GPS_o.csv</c> is the raw receiver rate
/// (25 Hz on MyChron 6); <c>_GPS.csv</c> is a 10 Hz decimation. Because <c>itow</c> gives exact UTC,
/// several sessions (the MyChron restarts a session whenever the kart slows/stops) can be discovered
/// and merged on one continuous timeline, see <see cref="TrackBuilder"/>.
/// </summary>
public static class RaceStudioGpsCsv
{
    /// <summary>Candidate GPS files inside a "_CSV" folder, highest rate first.</summary>
    public static readonly string[] GpsFileNames = { "_GPS_o.csv", "_GPS.csv" };

    /// <summary>
    /// Resolve one input (a GPS csv, a single "_CSV" folder, or a PARENT folder containing several)
    /// to the list of GPS csv files it covers. A parent folder is searched recursively.
    /// </summary>
    public static List<string> DiscoverGpsFiles(string input, bool gps10 = false)
    {
        var order = gps10 ? new[] { "_GPS.csv", "_GPS_o.csv" } : GpsFileNames;

        if (File.Exists(input))
        {
            // A GPS csv is used as-is. Any other channel file (AccelerometerX.csv, GyroZ.csv, RPM.csv,
            // ...) stands in for its session, so picking any file from a "_CSV" folder just works.
            string name = Path.GetFileName(input);
            if (GpsFileNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                return new List<string> { input };

            string? parent = Path.GetDirectoryName(Path.GetFullPath(input));
            if (parent is not null)
                foreach (var n in order)
                {
                    string p = Path.Combine(parent, n);
                    if (File.Exists(p)) return new List<string> { p };
                }
            throw new FileNotFoundException(
                $"'{name}' is not a GPS CSV, and its folder has no _GPS_o.csv / _GPS.csv:\n{parent}");
        }

        if (!Directory.Exists(input)) throw new FileNotFoundException($"Input not found: {input}");

        // A single session folder contains the GPS file directly.
        foreach (var n in order)
        {
            string p = Path.Combine(input, n);
            if (File.Exists(p)) return new List<string> { p };
        }
        // Otherwise treat it as a parent folder and gather every session beneath it.
        foreach (var n in order)
        {
            var found = Directory.EnumerateFiles(input, n, SearchOption.AllDirectories).ToList();
            if (found.Count > 0) { found.Sort(StringComparer.OrdinalIgnoreCase); return found; }
        }
        throw new FileNotFoundException($"No GPS CSV (_GPS_o.csv / _GPS.csv) found under:\n{input}");
    }

    /// <summary>Discover + parse every session covered by the given inputs (deduped).</summary>
    public static List<GpsSession> LoadAll(IEnumerable<string> inputs, bool gps10 = false)
    {
        var files = new List<string>();
        foreach (var input in inputs)
            foreach (var f in DiscoverGpsFiles(input, gps10))
                if (!files.Any(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase)))
                    files.Add(f);

        if (files.Count == 0) throw new FileNotFoundException("No GPS CSV files found.");

        var sessions = new List<GpsSession>();
        foreach (var f in files)
        {
            var s = Parse(f);
            s.DateHint = ExtractDateHint(f);
            s.Name = ExtractName(f);
            sessions.Add(s);
        }
        return sessions;
    }

    public static GpsSession Parse(string gpsFile)
    {
        var s = new GpsSession { SourceFile = gpsFile };
        using var reader = new StreamReader(gpsFile);

        var header = (reader.ReadLine() ?? "").Split(',').Select(x => x.Trim()).ToArray();
        int iT = Col(header, "time"), iI = Col(header, "itow"), iLa = Col(header, "lat"),
            iLo = Col(header, "lon"), iAl = Col(header, "alt"), iSp = Col(header, "speed"), iAc = Col(header, "accuracy");
        if (iT < 0 || iLa < 0 || iLo < 0)
            throw new FormatException($"'{Path.GetFileName(gpsFile)}' is not a RaceStudio GPS CSV (expected columns time, itow, lat, lon, ...).");

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split(',');
            if (f.Length <= iLo) continue;

            double G(int i) => i >= 0 && i < f.Length &&
                double.TryParse(f[i].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

            double t = G(iT), la = G(iLa), lo = G(iLo);
            if (double.IsNaN(t) || double.IsNaN(la) || double.IsNaN(lo)) continue; // skip units/blank rows

            s.Samples.Add(new GpsSample
            {
                TimeRel = t, ItowMs = G(iI), Lat = la, Lon = lo,
                Alt = G(iAl), SpeedMs = G(iSp), AccuracyM = G(iAc),
            });
        }

        if (s.Samples.Count > 1)
        {
            double span = s.Samples[^1].TimeRel - s.Samples[0].TimeRel;
            if (span > 0) s.NativeHz = (s.Samples.Count - 1) / span;
        }
        return s;
    }

    private static int Col(string[] cols, string name)
    {
        for (int i = 0; i < cols.Length; i++)
            if (string.Equals(cols[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>RaceStudio exports under ...\data\YYYY-MM-DD\Racer\Track\...; pull that date, else the file's write time.</summary>
    internal static DateTime? ExtractDateHint(string gpsFile)
    {
        foreach (var part in gpsFile.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (DateTime.TryParseExact(part, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return DateTime.SpecifyKind(d, DateTimeKind.Utc);
        try { return File.GetLastWriteTimeUtc(gpsFile); } catch { return null; }
    }

    internal static string ExtractName(string gpsFile)
    {
        string dir = Path.GetFileName(Path.GetDirectoryName(gpsFile) ?? "");
        return string.IsNullOrEmpty(dir) ? Path.GetFileNameWithoutExtension(gpsFile) : dir;
    }
}
