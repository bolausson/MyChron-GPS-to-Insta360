using System.Globalization;
using System.Linq;
using MyChron2Insta360.Core;

namespace MyChron2Insta360.App;

/// <summary>Headless command-line front-end. Any option flag routes here from <see cref="Program"/>.</summary>
internal static class Cli
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Any(a => a is "-h" or "--help")) { PrintHelp(); return 0; }
            if (args.Any(a => a == "--version")) { Console.WriteLine($"MyChron2Insta360 {AppInfo.Version}"); return 0; }

            var inputs = new List<string>();
            string? output = null, outDir = null, format = "gpx", dateStr = null;
            double? nudge = null, hz = null, maxAcc = null;
            int? leap = null;
            bool trim = false, gps10 = false, merge = false, writeImuCsv = true;
            double imuHz = 0; // 0 = native rate

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string? Next() => i + 1 < args.Length ? args[++i] : null;
                switch (a)
                {
                    case "-o": case "--out": output = Next(); break;
                    case "--out-dir": outDir = Next(); break;
                    case "--format": format = (Next() ?? "gpx").ToLowerInvariant(); break;
                    case "--fit": format = "fit"; break;
                    case "--both": format = "both"; break;
                    case "--merge": merge = true; break;
                    case "--gps10": gps10 = true; break;
                    case "--hz": hz = ParseD(Next()); break;
                    case "--nudge": nudge = ParseD(Next()); break;
                    case "--max-accuracy": maxAcc = ParseD(Next()); break;
                    case "--date": dateStr = Next(); break;
                    case "--leap": leap = (int?)ParseD(Next()); break;
                    case "--trim": trim = true; break;
                    case "--no-imu-csv": writeImuCsv = false; break;
                    case "--imu-hz": imuHz = ParseD(Next()) ?? 0; break;
                    case "--nogui": break;
                    default:
                        if (a.Length > 0 && a[0] != '-') inputs.Add(a);
                        break;
                }
            }

            if (inputs.Count == 0) { Console.Error.WriteLine("Error: no input (a RaceStudio _CSV folder, a parent folder, or _GPS*.csv).\n"); PrintHelp(); return 2; }
            foreach (var inp in inputs)
                if (!File.Exists(inp) && !Directory.Exists(inp)) { Console.Error.WriteLine($"Error: input not found: {inp}"); return 2; }
            if (format is not ("gpx" or "fit" or "both")) { Console.Error.WriteLine($"Error: --format must be gpx, fit or both (got '{format}')."); return 2; }

            var opt = new ConversionOptions { TrimStandstill = trim };
            if (nudge is double ms) opt.ManualOffsetSeconds = ms;
            if (hz is double h) opt.TargetHz = h;
            if (maxAcc is double ma) opt.MaxAccuracyM = ma;
            if (leap is int lp) opt.LeapSeconds = lp;
            if (!string.IsNullOrWhiteSpace(dateStr))
            {
                if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                { Console.Error.WriteLine("Error: --date must be YYYY-MM-DD."); return 2; }
                opt.DateOverrideUtc = DateTime.SpecifyKind(d, DateTimeKind.Utc);
            }

            // Insta360 video input (.insv/.lrv) uses the native trailer parser instead of the CSV pipeline.
            if (inputs.Count == 1 && Insta360Trailer.HasTrailer(inputs[0]))
                return RunInsta360(inputs[0], output, outDir, format, writeImuCsv, imuHz);

            var sessions = RaceStudioGpsCsv.LoadAll(inputs, gps10);
            Console.WriteLine($"Found {sessions.Count} session(s):");
            foreach (var s in sessions)
                Console.WriteLine($"  {DirName(Path.GetDirectoryName(s.SourceFile)!)}  ({s.Samples.Count} fixes, {s.NativeHz:0.#} Hz)");
            if (outDir is not null) Directory.CreateDirectory(outDir);

            if (merge && sessions.Count > 1)
            {
                Console.WriteLine("Mode: MERGE → one output file.");
                var segs = TrackBuilder.Build(sessions, opt);
                ReportBuild(opt);
                string primary = inputs[0];
                string baseName = MergedBaseName(opt);
                string dir = outDir ?? (Directory.Exists(primary) ? primary : Path.GetDirectoryName(sessions[0].SourceFile)!);
                string name = $"{baseName} ({sessions.Count} sessions)";
                string mergeBase = output ?? Path.Combine(dir, baseName);
                WriteJob(segs, mergeBase, name, format);
                if (writeImuCsv)
                {
                    var all = new List<ImuSample>();
                    foreach (var s in sessions)
                    {
                        var im = RaceStudioImu.Load(Path.GetDirectoryName(s.SourceFile)!);
                        if (imuHz > 0) im = ImuCsvWriter.Downsample(im, imuHz);
                        if (SessionStartUtc(s, opt) is DateTime su) foreach (var x in im) x.Utc = su.AddSeconds(x.ElapsedS);
                        all.AddRange(im);
                    }
                    if (all.Count > 0)
                    {
                        all.Sort((a, b) => (a.Utc ?? DateTime.MinValue).CompareTo(b.Utc ?? DateTime.MinValue));
                        string csv = mergeBase + "_imu.csv";
                        ImuCsvWriter.Write(csv, all);
                        Console.WriteLine($"Wrote {csv}  ({all.Count} IMU samples)");
                    }
                }
            }
            else
            {
                if (sessions.Count > 1) Console.WriteLine("Mode: individual → one output file per session (use --merge to combine).");
                foreach (var s in sessions)
                {
                    var segs = TrackBuilder.Build(new[] { s }, opt);
                    string folder = Path.GetDirectoryName(s.SourceFile)!;
                    string baseName = DirName(folder);
                    string dir = outDir ?? folder;
                    string basePath = (output is not null && sessions.Count == 1) ? output : Path.Combine(dir, baseName);
                    Console.WriteLine($"[{baseName}] {opt.TotalPoints} pts @ {opt.EffectiveHz:0.#} Hz" +
                                      (opt.StartUtc is DateTime t0 ? $"  {t0:HH:mm:ss}Z.." + (opt.EndUtc is DateTime t1 ? $"{t1:HH:mm:ss}Z" : "") : ""));
                    var extras = LoadMyChronExtras(s, opt, imuHz, out var imu);
                    WriteJob(segs, basePath, s.Name, format, extras);
                    if (extras.Laps is { Count: > 0 } lapList) Console.WriteLine($"  {lapList.Count} lap(s) from the logger.");
                    if (writeImuCsv && imu.Count > 0)
                    {
                        string csv = basePath + "_imu.csv";
                        ImuCsvWriter.Write(csv, imu);
                        Console.WriteLine($"Wrote {csv}  ({imu.Count} IMU samples)");
                    }
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Load a MyChron session's IMU, laps and temperature, all stamped to absolute UTC.</summary>
    private static FitWriter.Extras LoadMyChronExtras(GpsSession s, ConversionOptions opt, double imuHz, out List<ImuSample> imu)
    {
        string folder = Path.GetDirectoryName(s.SourceFile)!;
        DateTime? startUtc = SessionStartUtc(s, opt);
        imu = RaceStudioImu.Load(folder);
        if (imuHz > 0) imu = ImuCsvWriter.Downsample(imu, imuHz);
        if (startUtc is DateTime su)
            foreach (var x in imu) x.Utc = su.AddSeconds(x.ElapsedS);
        return new FitWriter.Extras
        {
            Imu = imu.Count > 0 ? imu : null,
            Laps = RaceStudioChannels.LoadLaps(folder, startUtc),
            Temperature = RaceStudioChannels.LoadTemperature(folder, startUtc),
        };
    }

    private static DateTime? SessionStartUtc(GpsSession s, ConversionOptions opt)
    {
        DateTime? dateHint = opt.DateOverrideUtc ?? s.DateHint;
        if (dateHint is null) return null;
        foreach (var g in s.Samples)
            if (!double.IsNaN(g.ItowMs) && g.ItowMs > 0)
                return ItowTime.ToUtc(g.ItowMs, dateHint.Value, opt.LeapSeconds).AddSeconds(-g.TimeRel);
        return null;
    }

    private static int RunInsta360(string input, string? output, string? outDir, string format, bool writeImuCsv, double imuHz)
    {
        var tel = Insta360Trailer.Parse(input);
        Console.WriteLine($"Insta360 {tel.Model ?? "?"} ({tel.Serial ?? "?"}): {tel.Gps.Count} GPS points, {tel.Imu.Count} IMU samples");
        if (tel.Gps.Count == 0 && tel.Imu.Count == 0)
        { Console.Error.WriteLine("No GPS or IMU telemetry in this file (was a GPS remote / phone connected while recording?)."); return 1; }

        // Anchor IMU to absolute UTC via the first GPS fix (approximate; exact offset needs the metadata record).
        if (tel.Gps.Count > 0)
        {
            DateTime g0 = tel.Gps[0].Utc;
            foreach (var s in tel.Imu) s.Utc = g0.AddSeconds(s.ElapsedS);
        }

        if (outDir is not null) Directory.CreateDirectory(outDir);
        string dir = outDir ?? Path.GetDirectoryName(Path.GetFullPath(input))!;
        string baseName = Path.GetFileNameWithoutExtension(input);
        string basePath = output ?? Path.Combine(dir, baseName);

        if (tel.Gps.Count > 0)
        {
            var segs = new List<List<TrackPoint>> { tel.Gps };
            Console.WriteLine($"  GPS UTC span: {tel.Gps[0].Utc:yyyy-MM-dd HH:mm:ss}Z .. {tel.Gps[^1].Utc:HH:mm:ss}Z");
            var fitImu = imuHz > 0 ? ImuCsvWriter.Downsample(tel.Imu, imuHz) : tel.Imu;
            var extras = new FitWriter.Extras { Imu = fitImu.Count > 0 ? fitImu : null };
            foreach (var (p, isFit) in FormatPaths(basePath, format))
            {
                if (isFit) FitWriter.Write(p, segs, baseName, extras);
                else GpxWriter.Write(p, segs, baseName);
                Console.WriteLine($"Wrote {p}");
            }
        }
        else Console.WriteLine("  (no GPS in this file; skipping GPX/FIT)");

        if (writeImuCsv && tel.Imu.Count > 0)
        {
            var csvImu = imuHz > 0 ? ImuCsvWriter.Downsample(tel.Imu, imuHz) : tel.Imu;
            string csv = basePath + "_imu.csv";
            ImuCsvWriter.Write(csv, csvImu);
            double span = csvImu[^1].ElapsedS;
            Console.WriteLine($"Wrote {csv}  ({csvImu.Count} IMU samples, {span:0.#}s, ~{(span > 0 ? (csvImu.Count - 1) / span : 0):0} Hz)");
        }
        return 0;
    }

    private static void ReportBuild(ConversionOptions opt)
    {
        Console.WriteLine($"  {opt.TotalPoints} points from {opt.SessionCount} session(s).");
        Console.WriteLine($"  Time base: {(opt.UsedItow ? "iTOW (exact GPS time)" : "relative (no iTOW, sync will need a nudge)")}");
        Console.WriteLine($"  Output rate: {(opt.TargetHz > 0 ? $"{opt.EffectiveHz:0.#} Hz (downsampled)" : $"{opt.EffectiveHz:0.#} Hz (native)")}");
        if (opt.StartUtc is DateTime a0 && opt.EndUtc is DateTime a1)
            Console.WriteLine($"  UTC span: {a0:yyyy-MM-dd HH:mm:ss}Z .. {a1:HH:mm:ss}Z");
    }

    private static void WriteJob(List<List<TrackPoint>> segments, string basePath, string trackName, string format,
                                 FitWriter.Extras? extras = null)
    {
        foreach (var (path, isFit) in FormatPaths(basePath, format))
        {
            if (isFit) FitWriter.Write(path, segments, trackName, extras);
            else GpxWriter.Write(path, segments, trackName);
            Console.WriteLine($"Wrote {path}");
        }
    }

    private static IEnumerable<(string path, bool isFit)> FormatPaths(string basePath, string format)
    {
        if (format == "gpx") { yield return (Ext(basePath, ".gpx"), false); yield break; }
        if (format == "fit") { yield return (Ext(basePath, ".fit"), true); yield break; }
        yield return (Ext(basePath, ".gpx"), false);
        yield return (Ext(basePath, ".fit"), true);
    }

    private static string DirName(string path) =>
        new DirectoryInfo(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

    /// <summary>Merged output base name: "merged-YYYYMMDD-HHMMSS" from the earliest GPS fix (UTC).</summary>
    internal static string MergedBaseName(ConversionOptions opt) =>
        "merged-" + (opt.StartUtc is DateTime st ? st.ToString("yyyyMMdd-HHmmss") : "unknown");

    private static string Ext(string p, string ext) => Path.HasExtension(p) ? Path.ChangeExtension(p, ext) : p + ext;

    private static double? ParseD(string? s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static void PrintHelp()
    {
        Console.WriteLine(
$@"MyChron2Insta360 {AppInfo.Version} - convert RaceStudio3 GPS CSV exports to GPX/FIT for the Insta360 Stats Dashboard.

Inputs (auto-detected):
  * RaceStudio '..._CSV' folder(s), a PARENT folder holding several, or a _GPS_o.csv / _GPS.csv.
    Each session becomes its own output file; --merge combines them onto one timeline via GPS 'itow'.
  * An Insta360 video (.insv / .lrv) recorded with a GPS remote or the phone app connected.

What each output holds:
  GPX  GPS track only: position, altitude, per-point speed and time. Best for maps and Insta360.
  FIT  GPS track PLUS accelerometer and gyroscope, temperature and lap splits (where available).
  CSV  Accelerometer (g) and gyroscope (deg/s) only, one row per sample, for analysis in other apps.

Usage:
  MyChron2Insta360 <folder> [more folders...] [options]   CLI mode (any option triggers CLI)
  MyChron2Insta360                                          no args -> GUI
  MyChron2Insta360 <folder>                                 drag onto the .exe -> GUI, preloaded

Options:
  --merge                   Merge all sessions into ONE output (default: one file per session).
  -o, --out <file>          Explicit output path (single-output only; extension set per format).
  --out-dir <folder>        Write outputs into this folder (created if needed).
  --format <gpx|fit|both>   Output format(s). Default gpx. (--fit / --both are shortcuts.)
  --gps10                   Use _GPS.csv (10 Hz) instead of _GPS_o.csv (25 Hz).
  --hz <n>                  Downsample to n Hz. Default: keep the native rate.
  --nudge <seconds>         Add seconds to every timestamp (fine sync tuning).
  --max-accuracy <m>        Drop fixes with reported accuracy worse than m metres (default: keep all).
  --date <YYYY-MM-DD>       Override the session date (only if it isn't in the folder path).
  --leap <n>                GPS-UTC leap seconds for iTOW (default {ItowTime.DefaultLeapSeconds}).
  --trim                    Trim leading/trailing standstill per session (off by default).
  --no-imu-csv              Do not write the separate accel/gyro CSV (it is written by default).
  --imu-hz <n>              Downsample the IMU to n Hz (default: native, ~1000 Hz on Insta360).
  -h, --help                Show this help.
  --version                 Show version.

Import the output in the Insta360 app: Dashboard -> Data Source -> Local Files -> Import.");
    }
}
