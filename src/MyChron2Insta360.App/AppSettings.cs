using System.Globalization;
using System.Text;

namespace MyChron2Insta360.App;

/// <summary>
/// The settings the GUI remembers and the CLI can start from, stored as a small ini file next to the
/// exe ("MyChron2Insta360.ini" unless another path is given).
///
/// Keys are named after the command line options they stand for, so a file written by the GUI reads as
/// documentation of the equivalent command, which is what makes it useful for scripting. The exception
/// is the Sources pickers' default folder, which only the GUI has. Nothing is written unless you ask for
/// it: with no ini present the program behaves exactly as it always has.
/// </summary>
internal sealed class AppSettings
{
    // Output
    public string Format = "gpx,fit,telemetry-overlay";
    public bool ImuCsv = true;
    public string OutDir = "";

    // Track building
    public double Nudge;
    public bool NativeRate = true;
    public double Hz = 25;
    public double MaxAccuracy;
    public double ImuHz;
    public bool Merge;
    public bool Trim;

    /// <summary>"off", "auto", or an angle in degrees.</summary>
    public string Tilt = "off";

    // Telemetry Overlay, CLI only
    public double ToRate = 50;
    public bool ToInvertSteering = true;
    public bool ToInvertLateral;
    public bool PreferXrk = true;

    // Sources pickers, GUI only
    /// <summary>Where RaceStudio 3 keeps the sessions it downloads, unless told otherwise.</summary>
    public const string RaceStudioDataDir = @"C:\AIM_SPORT\RaceStudio3\user\data";
    public string SourcesDir = RaceStudioDataDir;
    public bool AlwaysOpenSourcesDir;

    /// <summary>
    /// The Sources pickers' default folder: the one typed, or RaceStudio's when the box is empty.
    /// Surrounding quotes are dropped, because Explorer's "Copy as path" adds them.
    /// </summary>
    public static string SourcesDirOrDefault(string? typed)
    {
        string dir = (typed ?? "").Trim().Trim('"').Trim();
        return dir.Length > 0 ? dir : RaceStudioDataDir;
    }

    /// <summary>Where the settings live by default: beside the exe, named after it.</summary>
    public static string DefaultPath
    {
        get
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return Path.Combine(AppContext.BaseDirectory, "MyChron2Insta360.ini");
            return Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + ".ini");
        }
    }

    public static bool Exists(string? path) => File.Exists(Resolve(path));

    /// <summary>
    /// Which file to actually read. With no path given this is the default one beside the exe, unless
    /// that file only holds a "settings-file = ..." line, which points somewhere else. That pointer is
    /// what lets a settings file live outside a read-only install folder and still be found on the next
    /// start, without the program needing a registry key or a hidden profile directory.
    /// </summary>
    public static string Resolve(string? path) => Resolve(path, out _);

    /// <param name="viaPointer">True when the answer came from a "settings-file" redirect, which lets
    /// the caller say so when the file it points at is missing rather than quietly using defaults.</param>
    public static string Resolve(string? path, out bool viaPointer)
    {
        viaPointer = false;
        if (!string.IsNullOrWhiteSpace(path)) return path!;
        string def = DefaultPath;
        try
        {
            if (!File.Exists(def)) return def;
            foreach (var raw in File.ReadAllLines(def))
            {
                string line = raw.Trim();
                if (!line.StartsWith("settings-file", StringComparison.OrdinalIgnoreCase)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string target = line[(eq + 1)..].Trim();
                // Returned even when it is missing: a settings file on a network or sync folder that
                // has not arrived yet must be reported, not silently replaced by the defaults.
                if (target.Length > 0) { viaPointer = true; return target; }
            }
        }
        catch (IOException) { /* fall back to the default file */ }
        return def;
    }

    /// <summary>Read a settings file. A missing file simply yields the built-in defaults.</summary>
    public static AppSettings Load(string? path, out string used, out string? error)
    {
        used = Resolve(path, out bool viaPointer);
        error = null;
        var s = new AppSettings();
        if (!File.Exists(used))
        {
            if (viaPointer)
                error = $"{DefaultPath} points here, but the file is not there. Using the built-in defaults.";
            return s;
        }

        try
        {
            foreach (var raw in File.ReadAllLines(used))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';' or '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim().ToLowerInvariant();
                string value = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "format": s.Format = value; break;
                    case "imu-csv": s.ImuCsv = Bool(value, s.ImuCsv); break;
                    case "out-dir": s.OutDir = value; break;
                    case "nudge": s.Nudge = Num(value, s.Nudge); break;
                    case "rate":
                        s.NativeRate = value.Equals("native", StringComparison.OrdinalIgnoreCase);
                        if (!s.NativeRate) s.Hz = Num(value, s.Hz);
                        break;
                    case "max-accuracy": s.MaxAccuracy = Num(value, s.MaxAccuracy); break;
                    case "imu-hz": s.ImuHz = Num(value, s.ImuHz); break;
                    case "tilt": s.Tilt = value; break;
                    case "merge": s.Merge = Bool(value, s.Merge); break;
                    case "trim": s.Trim = Bool(value, s.Trim); break;
                    case "to-rate": s.ToRate = Num(value, s.ToRate); break;
                    case "to-invert-steering": s.ToInvertSteering = Bool(value, s.ToInvertSteering); break;
                    case "to-invert-lateral": s.ToInvertLateral = Bool(value, s.ToInvertLateral); break;
                    case "prefer-xrk": s.PreferXrk = Bool(value, s.PreferXrk); break;
                    case "sources-dir": s.SourcesDir = value; break;
                    case "always-open-sources-dir": s.AlwaysOpenSourcesDir = Bool(value, s.AlwaysOpenSourcesDir); break;
                }
            }
        }
        catch (Exception e) { error = e.Message; }
        return s;
    }

    public void Save(string? path, out string used)
    {
        used = string.IsNullOrWhiteSpace(path) ? DefaultPath : path!;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(used))!);
        File.WriteAllText(used, ToIni(), new UTF8Encoding(false));

        // Saving somewhere else leaves a pointer at the default location, so the next start finds it.
        if (!string.Equals(Path.GetFullPath(used), Path.GetFullPath(DefaultPath), StringComparison.OrdinalIgnoreCase))
            File.WriteAllText(DefaultPath,
                "# This program's settings live elsewhere. Edit or delete the line below to change that.\r\n" +
                $"settings-file = {Path.GetFullPath(used)}\r\n", new UTF8Encoding(false));
    }

    /// <summary>The file's text, exactly as <see cref="Save"/> writes it.</summary>
    internal string ToIni()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# MyChron2Insta360 settings.");
        sb.AppendLine("# Written by 'Save as default'. Every key matches the command line option of the");
        sb.AppendLine("# same name, so this doubles as a record of the equivalent command.");
        sb.AppendLine("# Delete this file to go back to the built-in defaults.");
        sb.AppendLine();
        sb.AppendLine("[MyChron2Insta360]");
        sb.AppendLine($"format = {Format}");
        sb.AppendLine($"imu-csv = {Low(ImuCsv)}");
        sb.AppendLine($"out-dir = {OutDir}");
        sb.AppendLine($"rate = {(NativeRate ? "native" : Hz.ToString("0.###", ci))}");
        sb.AppendLine($"imu-hz = {ImuHz.ToString("0.###", ci)}");
        sb.AppendLine($"nudge = {Nudge.ToString("0.###", ci)}");
        sb.AppendLine($"max-accuracy = {MaxAccuracy.ToString("0.###", ci)}");
        sb.AppendLine($"tilt = {Tilt}");
        sb.AppendLine($"merge = {Low(Merge)}");
        sb.AppendLine($"trim = {Low(Trim)}");
        sb.AppendLine($"prefer-xrk = {Low(PreferXrk)}");
        sb.AppendLine();
        sb.AppendLine("# Telemetry Overlay export");
        sb.AppendLine($"to-rate = {ToRate.ToString("0.###", ci)}");
        sb.AppendLine($"to-invert-steering = {Low(ToInvertSteering)}");
        sb.AppendLine($"to-invert-lateral = {Low(ToInvertLateral)}");
        sb.AppendLine();
        sb.AppendLine("# GUI only: where 'Add folder...' and 'Add file(s)...' start until Windows remembers");
        sb.AppendLine("# a pick, or every time when always-open-sources-dir is true");
        sb.AppendLine($"sources-dir = {SourcesDir}");
        sb.AppendLine($"always-open-sources-dir = {Low(AlwaysOpenSourcesDir)}");
        return sb.ToString();
    }

    private static string Low(bool b) => b ? "true" : "false";

    private static bool Bool(string v, bool fallback) => v.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => fallback,
    };

    /// <summary>Accepts a dot or a comma, like every other number this program reads.</summary>
    private static double Num(string v, double fallback) => Cli.ParseD(v) ?? fallback;
}
