using System.Globalization;
using System.Text.RegularExpressions;

namespace MyChron2Insta360.Core;

/// <summary>What a .vbo says about itself beyond the fixes, for the log.</summary>
public sealed class VboInfo
{
    /// <summary>The program and device that wrote it, from the comments (Dragy names both).</summary>
    public string Writer = "", Model = "";
    /// <summary>Where the date of the first fix came from.</summary>
    public string DateSource = "";
    /// <summary>Rows left out: unreadable, without a fix, or out of step with the clock.</summary>
    public int SkippedRows;
    public int MidnightRollovers;

    public string Describe()
    {
        var parts = new List<string>();
        if (Writer.Length > 0) parts.Add(Writer);
        if (Model.Length > 0) parts.Add(Model);
        if (DateSource.Length > 0) parts.Add("date from " + DateSource);
        if (MidnightRollovers > 0) parts.Add("crosses midnight UTC");
        if (SkippedRows > 0) parts.Add($"{SkippedRows} row(s) skipped");
        return string.Join(", ", parts);
    }
}

/// <summary>Everything one .vbo yields: a GPS session and a little about where it came from.</summary>
public sealed class VboData
{
    public GpsSession Gps { get; } = new();
    public VboInfo Info { get; } = new();
}

/// <summary>
/// Reads a Racelogic VBOX ".vbo" text file, the format the Dragy Lap app exports and many loggers and
/// lap timers write. Its GPS columns are read: UTC time of day, position, speed, heading, height and
/// the satellite count, under "[header]", "[column names]" and "[data]" sections. A Dragy file holds
/// nothing else; other channels some loggers add are left out.
///
/// Three conventions decide whether a track lands in the right place: positions are in minutes,
/// longitude counts positive WEST (Racelogic's reversed hemisphere), and the time column is UTC time of
/// day, with the date only in the header lines. Dragy's own .dlap is a different, undocumented format
/// and cannot be read; the same session exported as .vbo can.
/// </summary>
public static class VboReader
{
    public const string Extension = ".vbo";
    public const string DlapExtension = ".dlap";

    private const long MsPerDay = 86_400_000;

    public static bool IsVboFile(string path) =>
        Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    public static bool IsDlapFile(string path) =>
        Path.GetExtension(path).Equals(DlapExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every .vbo under a folder, searched recursively, in name order.</summary>
    public static IEnumerable<string> FindIn(string folder) => Find(folder, Extension);

    /// <summary>Every Dragy .dlap under a folder, only ever used to explain why nothing was found.</summary>
    public static IEnumerable<string> FindDlapIn(string folder) => Find(folder, DlapExtension);

    private static IEnumerable<string> Find(string folder, string extension) =>
        Directory.EnumerateFiles(folder, "*" + extension, SearchOption.AllDirectories)
                 .Where(f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>What a .dlap is, and what to do instead. The first line is what the GUI shows.</summary>
    public static string DlapMessage(string path) =>
        "A Dragy .dlap cannot be read: export the session as .vbo in the Dragy Lap app instead.\n" +
        $"'{Path.GetFileName(path)}' is the Dragy app's own format, which is not documented.";

    /// <summary>The log line for a .dlap given alongside inputs that can be read.</summary>
    public static string DlapNote(string path) =>
        $"Note: skipping '{Path.GetFileName(path)}': a Dragy .dlap cannot be read; export the session as .vbo in the Dragy Lap app instead.";

    /// <summary>Why an IMU CSV that was asked for does not appear for a .vbo.</summary>
    public const string NoImuNote = "Note: only the GPS of a .vbo is read, so there is no IMU CSV for it.";

    /// <param name="dateUtc">The UTC date of the first fix, for a file whose header gives none (--date).</param>
    public static VboData Load(string path, DateTime? dateUtc = null)
    {
        using var reader = new StreamReader(path);
        var data = Parse(reader, Path.GetFileName(path), dateUtc);
        data.Gps.SourceFile = path;
        data.Gps.Name = Path.GetFileNameWithoutExtension(path);
        return data;
    }

    private readonly record struct Row(long Ms, double Lat, double Lon, double SpeedMs,
                                       double HeadingDeg, double Alt, double Sats);

    internal static VboData Parse(TextReader text, string fileName, DateTime? dateUtc)
    {
        var data = new VboData();
        var info = data.Info;
        string section = "";
        string? created = null, utcStarted = null;
        var headerNames = new List<string>();
        Columns? cols = null;
        var fixes = new List<Row>();

        while (text.ReadLine() is string raw)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[' && line[^1] == ']') { section = line[1..^1].Trim().ToLowerInvariant(); continue; }

            switch (section)
            {
                case "":
                    if (line.StartsWith("File created on", StringComparison.OrdinalIgnoreCase))
                        created = line["File created on".Length..];
                    break;
                case "header":
                    headerNames.Add(line);
                    break;
                case "comments":
                    if (After(line, "UTC Date Started:") is string started) utcStarted = started;
                    else if (After(line, "Generated by") is string writer) info.Writer = writer;
                    else if (After(line, "Model:") is string model) info.Model = model;
                    break;
                case "column names":
                    cols = Columns.From(Split(line), headerNames, fileName);
                    break;
                case "data":
                    // [column names] is required, but the [header] list says the same in longer words.
                    cols ??= Columns.From(headerNames.Select(h => Split(h)[0]).ToArray(), headerNames, fileName);
                    // A logger writes rows at 0,0 before its first fix; they carry no position or clock.
                    if (cols.TryRead(Split(line), out var r) && !(r.Lat == 0 && r.Lon == 0)) fixes.Add(r);
                    else info.SkippedRows++;
                    break;
            }
        }

        if (cols is null || fixes.Count == 0)
            throw new InvalidDataException($"'{fileName}' holds no GPS fixes, so it is not a VBO log that can be read.");
        var rows = Timeline(fixes, info);
        if (rows.Count < 2)
            throw new InvalidDataException($"'{fileName}' holds fewer than two usable GPS fixes, so there is no track in it.");

        long firstMs = rows[0].Ms;
        DateTime firstDay = ResolveDay(dateUtc, utcStarted, created, fileName, firstMs, rows[0].Lon, info);

        var s = data.Gps;
        s.StartUtc = DateTime.SpecifyKind(firstDay, DateTimeKind.Utc).AddMilliseconds(firstMs);
        s.DateHint = s.StartUtc.Value.Date;
        foreach (var r in rows)
            s.Samples.Add(new GpsSample
            {
                TimeRel = (r.Ms - firstMs) / 1000.0,
                ItowMs = double.NaN,
                Lat = r.Lat,
                Lon = r.Lon,
                Alt = r.Alt,
                SpeedMs = r.SpeedMs,
                AccuracyM = double.NaN,   // a .vbo reports none, so an accuracy filter keeps every fix
                HeadingDeg = r.HeadingDeg,
                Satellites = r.Sats,
            });
        double span = s.Samples[^1].TimeRel;
        if (s.Samples.Count > 1 && span > 0) s.NativeHz = (s.Samples.Count - 1) / span;
        return data;
    }

    /// <summary>
    /// Put the fixes on one clock that only moves forward, as milliseconds from midnight of the first
    /// fix's day. Each row is judged against the last row kept and the rows after it, so a single stray
    /// row, whatever time it carries, is dropped rather than taking the clock with it: a row that the
    /// next two both fall back behind is a stray, not the clock moving on, and a step back across
    /// midnight only starts a new day when the rows after it agree.
    /// </summary>
    private static List<Row> Timeline(List<Row> fixes, VboInfo info)
    {
        int first = 0;
        // The first fix sets the clock and the day, so it has to fit the two that follow it.
        while (first + 2 < fixes.Count
               && Step(fixes[first].Ms, fixes[first + 1].Ms) > Math.Max(1000, 10 * Step(fixes[first + 1].Ms, fixes[first + 2].Ms)))
        { info.SkippedRows++; first++; }

        var kept = new List<Row>(fixes.Count - first) { fixes[first] };
        for (int i = first + 1; i < fixes.Count; i++)
        {
            long prev = kept[^1].Ms;
            long step = Step(prev, fixes[i].Ms);
            // Backwards, a repeat, or half a day on: no single session does that.
            if (step >= MsPerDay / 2) { info.SkippedRows++; continue; }
            if (i + 1 < fixes.Count && Step(prev, fixes[i + 1].Ms) < step
                && (i + 2 >= fixes.Count || Step(prev, fixes[i + 2].Ms) < step))
            { info.SkippedRows++; continue; }
            long ms = prev + step;
            if (ms / MsPerDay > prev / MsPerDay) info.MidnightRollovers++;
            kept.Add(fixes[i] with { Ms = ms });
        }
        return kept;
    }

    /// <summary>How far after <paramref name="from"/> the next time this time of day comes round: more
    /// than zero, at most a day.</summary>
    private static long Step(long from, long toMsOfDay)
    {
        long step = (toMsOfDay - from % MsPerDay) % MsPerDay;
        return step <= 0 ? step + MsPerDay : step;
    }

    /// <summary>Which column is which, and how to turn one data row into a fix.</summary>
    private sealed class Columns
    {
        private int _time = -1, _lat = -1, _lon = -1, _speed = -1, _heading = -1, _height = -1, _sats = -1, _needed;
        private double _speedToMs = 1 / 3.6;

        public static Columns From(string[] names, IReadOnlyList<string> headerNames, string fileName)
        {
            var c = new Columns
            {
                _time = Index(names, "time"),
                _lat = Index(names, "lat", "latitude"),
                _lon = Index(names, "long", "lon", "lng", "longitude"),
                _speed = Index(names, "velocity", "speed"),
                _heading = Index(names, "heading", "course"),
                _height = Index(names, "height", "alt", "altitude"),
                _sats = Index(names, "sats", "satellites"),
            };
            if (c._time < 0 || c._lat < 0 || c._lon < 0)
                throw new InvalidDataException($"'{fileName}' has no time, lat and long columns, so it is not a VBO GPS log.");
            c._needed = new[] { c._time, c._lat, c._lon }.Max();

            // The unit is part of the channel's name in [header], e.g. "velocity kmh".
            string unit = headerNames.Select(Split)
                                     .Where(h => h.Length > 1 && h[0].ToLowerInvariant() is "velocity" or "speed")
                                     .Select(h => h[1].ToLowerInvariant()).FirstOrDefault() ?? "kmh";
            c._speedToMs = unit switch
            {
                "mph" => 0.44704,
                "knots" or "kts" or "knot" => 1852.0 / 3600.0,
                "m/s" or "ms" or "mps" => 1.0,
                _ => 1 / 3.6,
            };
            return c;
        }

        public bool TryRead(string[] f, out Row row)
        {
            row = default;
            if (f.Length <= _needed || !TryTimeOfDayMs(f[_time], out long ms)
                || !TryNum(f[_lat], out double latMin) || !TryNum(f[_lon], out double lonMin))
                return false;
            double heading = Opt(f, _heading);
            if (!double.IsNaN(heading)) heading = (heading % 360 + 360) % 360;
            double sats = Opt(f, _sats);
            // Racelogic hardware adds 64 while the brake trigger is pressed and 128 with DGPS.
            if (!double.IsNaN(sats)) sats = (int)sats & 63;
            row = new Row(ms, latMin / 60.0, -lonMin / 60.0,   // minutes, and longitude positive West
                          Opt(f, _speed) * _speedToMs, heading, Opt(f, _height), sats);
            return true;
        }

        private static double Opt(string[] f, int i) => i >= 0 && i < f.Length && TryNum(f[i], out double v) ? v : double.NaN;

        private static int Index(string[] names, params string[] wanted)
        {
            for (int i = 0; i < names.Length; i++)
                if (wanted.Contains(names[i], StringComparer.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    /// <summary>
    /// Milliseconds since midnight from "hhmmss.sss", read from the digits rather than through a double,
    /// in which 144151.840 is 144151.83999... and would lose a millisecond. A leading zero may be
    /// missing; anything that is not a time of day (minutes over 59, say) is refused.
    /// </summary>
    internal static bool TryTimeOfDayMs(string token, out long ms)
    {
        ms = 0;
        string t = token.TrimStart('+');
        int dot = t.IndexOf('.');
        string whole = dot < 0 ? t : t[..dot], frac = dot < 0 ? "" : t[(dot + 1)..];
        if (whole.Length is 0 or > 6 || !whole.All(char.IsAsciiDigit) || !frac.All(char.IsAsciiDigit)) return false;
        whole = whole.PadLeft(6, '0');
        int hh = int.Parse(whole[..2], CultureInfo.InvariantCulture);
        int mm = int.Parse(whole[2..4], CultureInfo.InvariantCulture);
        int ss = int.Parse(whole[4..], CultureInfo.InvariantCulture);
        if (hh > 23 || mm > 59 || ss > 60) return false;
        int fracMs = frac.Length == 0 ? 0 : int.Parse((frac + "00")[..3], CultureInfo.InvariantCulture);
        // A leap second (ss 60) is held at the last millisecond of the minute, so it cannot reach into
        // the next minute, or past midnight into the next day.
        if (ss == 60) { ss = 59; fracMs = 999; }
        ms = ((hh * 60L + mm) * 60 + ss) * 1000 + fracMs;
        return true;
    }

    private static readonly Regex HeaderStamp = new(
        @"(?<!\d)(?<d>\d{1,2})/(?<m>\d{1,2})/(?<y>\d{4})(?:\s*(?:@|at)?\s*(?<H>\d{1,2}):(?<M>\d{2})(?::(?<S>\d{2}))?)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NameStamp = new(
        @"(?<!\d)(?<y>20\d{2})[-_]?(?<m>\d{2})[-_]?(?<d>\d{2})(?:[-_ T]?(?<H>\d{2})[-_.]?(?<M>\d{2})(?:[-_.]?(?<S>\d{2}))?)?",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The UTC date of the first fix: Dragy's "UTC Date Started", then the "File created on" line, then
    /// --date, then a date in the file name, the weakest hint. Never the file's own time: a copied or
    /// shared file is stamped when it arrived, not when it was recorded.
    /// </summary>
    private static DateTime ResolveDay(DateTime? dateUtc, string? utcStarted, string? created, string fileName,
                                       long firstMs, double lonDeg, VboInfo info)
    {
        if (TryStamp(HeaderStamp, utcStarted, out var st, out bool hasTime))
        {
            info.DateSource = "'UTC Date Started'";
            return PickDay(st, hasTime, stampIsUtc: true, firstMs, lonDeg);
        }
        // Dragy writes UTC here, other writers may use local time; PickDay copes with either.
        if (TryStamp(HeaderStamp, created, out st, out hasTime))
        {
            info.DateSource = "'File created on'";
            return PickDay(st, hasTime, stampIsUtc: false, firstMs, lonDeg);
        }
        if (dateUtc is DateTime given) { info.DateSource = "--date"; return given.Date; }
        if (TryStamp(NameStamp, fileName, out st, out hasTime))
        {
            info.DateSource = "the file name";
            return PickDay(st, hasTime, stampIsUtc: false, firstMs, lonDeg);
        }
        throw new InvalidDataException(
            $"No date in '{fileName}': a .vbo records only the time of day, and neither its header nor its " +
            "name gives the day. Convert that file on its own on the command line with --date YYYY-MM-DD.");
    }

    private static bool TryStamp(Regex rx, string? text, out DateTime stamp, out bool hasTime)
    {
        stamp = default;
        hasTime = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (Match m in rx.Matches(text))
        {
            int Get(string g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
            int y = Get("y"), mo = Get("m"), d = Get("d"), H = Get("H"), M = Get("M"), S = Get("S");
            // An unset logger clock writes 1970 or year 0; such a date is no date at all.
            if (y is < 1990 or > 2100 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) continue;
            // Digits after the date that are not a time (a serial number, say) still leave the date.
            hasTime = m.Groups["H"].Success && H <= 23 && M <= 59 && S <= 59;
            stamp = hasTime ? new DateTime(y, mo, d, H, M, S) : new DateTime(y, mo, d);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The UTC day of the first fix from a stamp naming a day, and maybe a time. Of the stamp's day and
    /// its two neighbours, the one whose implied time zone is most plausible wins: none for a UTC stamp,
    /// else the solar offset of the track's longitude, which civil time never strays 12 h from. That
    /// keeps a session starting just before midnight UTC, or a local stamp just after it, on the right
    /// day. A local date with no time is the day the first fix falls on in solar time.
    /// </summary>
    internal static DateTime PickDay(DateTime stamp, bool hasTime, bool stampIsUtc, long firstMsOfDay, double lonDeg)
    {
        double expectedH = stampIsUtc ? 0 : lonDeg / 15.0;
        if (!hasTime)
        {
            double localH = firstMsOfDay / 3_600_000.0 + expectedH;
            return stamp.Date.AddDays(localH >= 24 ? -1 : localH < 0 ? 1 : 0);
        }
        DateTime best = stamp.Date;
        double bestErr = double.MaxValue;
        for (int d = -1; d <= 1; d++)
        {
            DateTime candidate = stamp.Date.AddDays(d);
            double offsetH = (stamp - candidate.AddMilliseconds(firstMsOfDay)).TotalHours;
            double err = Math.Abs(offsetH - expectedH);
            if (err < bestErr) { bestErr = err; best = candidate; }
        }
        return best;
    }

    private static string? After(string line, string prefix) =>
        line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? line[prefix.Length..].Trim() : null;

    private static string[] Split(string line) => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool TryNum(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
