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

            string? input = null, output = null, tz = null, format = "gpx";
            double? utcOffsetHours = null, nudge = null, hz = null;
            int minSat = 0;
            bool trim = false, forceLocal = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string? Next() => i + 1 < args.Length ? args[++i] : null;
                switch (a)
                {
                    case "-o": case "--out": output = Next(); break;
                    case "--format": format = (Next() ?? "gpx").ToLowerInvariant(); break;
                    case "--fit": format = "fit"; break;
                    case "--both": format = "both"; break;
                    case "--tz": tz = Next(); break;
                    case "--utc-offset": utcOffsetHours = ParseD(Next()); break;
                    case "--local": forceLocal = true; break;
                    case "--nudge": nudge = ParseD(Next()); break;
                    case "--hz": hz = ParseD(Next()); break;
                    case "--min-sat": minSat = (int)(ParseD(Next()) ?? 0); break;
                    case "--trim": trim = true; break;
                    case "--nogui": break;
                    default:
                        if (a.Length > 0 && a[0] != '-' && input is null) input = a;
                        break;
                }
            }

            if (input is null) { Console.Error.WriteLine("Error: no input CSV specified.\n"); PrintHelp(); return 2; }
            if (!File.Exists(input)) { Console.Error.WriteLine($"Error: file not found: {input}"); return 2; }
            if (format is not ("gpx" or "fit" or "both"))
            { Console.Error.WriteLine($"Error: --format must be gpx, fit or both (got '{format}')."); return 2; }

            bool usingAuto = utcOffsetHours is null && string.IsNullOrWhiteSpace(tz) && !forceLocal;

            var opt = new ConversionOptions { MinSatellites = minSat, TrimStandstill = trim };
            if (utcOffsetHours is double oh) opt.FixedUtcOffset = TimeSpan.FromHours(oh);
            else if (!string.IsNullOrWhiteSpace(tz)) opt.SessionTimeZone = ResolveTz(tz!);
            else if (forceLocal) opt.AutoTimeZone = false;
            if (nudge is double ms) opt.ManualOffsetSeconds = ms;
            if (hz is double h) opt.TargetHz = h;

            Console.WriteLine($"Parsing {input} ...");
            var data = AimCsvParser.Parse(input);
            Console.WriteLine($"  {data.Rows.Count} rows, {data.Channels.Count} channels, {data.SampleRateHz:0.#} Hz logging rate.");

            var pts = TrackBuilder.Build(data, opt);
            Console.WriteLine($"  Timezone: {DescribeZone(opt, usingAuto)}");
            Console.WriteLine($"  Output rate: {DescribeRate(opt)}");
            Console.WriteLine($"  {pts.Count} track points.");
            if (pts.Count > 0)
                Console.WriteLine($"  UTC span: {pts[0].Utc:yyyy-MM-dd HH:mm:ss}Z .. {pts[^1].Utc:HH:mm:ss}Z");
            else
                Console.Error.WriteLine("  WARNING: no valid GPS points produced. Check channels / satellite filter.");

            string name = data.GetMeta("Session") ?? Path.GetFileNameWithoutExtension(input);
            foreach (var (path, isFit) in OutputPaths(input, output, format))
            {
                if (isFit) FitWriter.Write(path, pts, name);
                else GpxWriter.Write(path, pts, name);
                Console.WriteLine($"Wrote {path}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static string DescribeRate(ConversionOptions opt)
    {
        string detected = opt.DetectedGpsHz is double d ? $"detected GPS {d:0.#} Hz" : "GPS rate not detected";
        return opt.TargetHz > 0
            ? $"{opt.EffectiveHz:0.#} Hz (manual — {detected})"
            : $"{opt.EffectiveHz:0.#} Hz (auto — matched to {detected})";
    }

    private static string DescribeZone(ConversionOptions opt, bool usingAuto)
    {
        if (opt.FixedUtcOffset is TimeSpan o)
            return $"UTC{(o < TimeSpan.Zero ? "-" : "+")}{Math.Abs(o.TotalHours):0.##}";
        if (opt.SessionTimeZone is TimeZoneInfo z)
            return z.Id + (usingAuto ? " (auto-detected from GPS)" : "");
        return TimeZoneInfo.Local.Id + (usingAuto ? " (auto-detect failed — using machine local)" : "");
    }

    private static IEnumerable<(string path, bool isFit)> OutputPaths(string input, string? output, string format)
    {
        if (format == "gpx") { yield return (output ?? Path.ChangeExtension(input, ".gpx"), false); yield break; }
        if (format == "fit") { yield return (output ?? Path.ChangeExtension(input, ".fit"), true); yield break; }
        string base_ = output ?? input; // both: derive .gpx and .fit from the same base
        yield return (Path.ChangeExtension(base_, ".gpx"), false);
        yield return (Path.ChangeExtension(base_, ".fit"), true);
    }

    private static double? ParseD(string? s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static TimeZoneInfo ResolveTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { throw new ArgumentException($"Unknown timezone id: '{id}'. Use an IANA id (e.g. 'Europe/Paris') or a Windows id."); }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
$@"MyChron2Insta360 {AppInfo.Version} - convert AiM/MyChron RaceStudio3 CSV to GPX/FIT for the Insta360 Stats Dashboard.

Usage:
  MyChron2Insta360 <input.csv> [options]     CLI mode (any option triggers CLI)
  MyChron2Insta360                           no args -> GUI
  MyChron2Insta360 <input.csv>               drag CSV onto the .exe -> GUI, preloaded

Options:
  -o, --out <file>          Output path (default: next to the input).
  --format <gpx|fit|both>   Output format(s). Default gpx. (--fit / --both are shortcuts.)
  --tz <IANA|Windows>       Session timezone, e.g. 'Europe/Paris'. Default: auto-detect from GPS.
  --utc-offset <hours>      Fixed UTC offset instead of a zone, e.g. 2. Overrides --tz.
  --local                   Use the machine's local timezone instead of auto-detecting.
  --nudge <seconds>         Add seconds to every timestamp (fine sync tuning).
  --hz <n>                  Output rate. Default: auto-match the detected GPS rate.
  --min-sat <n>             Drop points below this satellite count (default 0 = off).
  --trim                    Trim leading/trailing standstill (off by default).
  -h, --help                Show this help.
  --version                 Show version.

Import the output in the Insta360 app: Dashboard -> Data Source -> Local Files -> Import.");
    }
}
