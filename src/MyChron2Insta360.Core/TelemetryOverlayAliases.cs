using System.Reflection;
using System.Text;

namespace MyChron2Insta360.Core;

/// <summary>How a discovered channel ended up being emitted.</summary>
public enum ChannelBinding
{
    /// <summary>Name and units already matched the alias table, emitted unchanged.</summary>
    Alias,
    /// <summary>Renamed or re-united to hit a known alias (Internal Batt to Battery, and so on).</summary>
    Renamed,
    /// <summary>No alias exists (EGT, lambda, ...). Emitted verbatim; arrives as an extra stream.</summary>
    Extra,
}

/// <summary>The header and units a channel is written under, plus how that was decided.</summary>
public readonly record struct ChannelHeader(string Header, string Units, ChannelBinding Binding)
{
    public string Key => $"{Header} ({Units})";
}

/// <summary>
/// Telemetry Overlay binds a CSV column to a gauge when "header (units)" matches an entry in its
/// built-in alias table exactly. The table is extracted from the app and checked into this repo, and
/// embedded here so a single-file build can still look names up.
///
/// The table is deliberately inconsistent: "Logger Temperature (°C)" needs the degree sign while
/// "Coolant Temperature (C)" must not have it. So nothing here ever derives a unit string; every
/// candidate is looked up, and a rename is only used when the renamed key is really in the table.
/// </summary>
public static class TelemetryOverlayAliases
{
    private static readonly Lazy<HashSet<string>> Table = new(Load);

    public static int Count => Table.Value.Count;

    /// <summary>True when "header (units)" is a name Telemetry Overlay recognises.</summary>
    public static bool Contains(string key) => Table.Value.Contains(key);

    public static bool Contains(string header, string units) => Contains($"{header} ({units})");

    /// <summary>
    /// Renames that turn a RaceStudio channel name into one the alias table knows, from the spec's
    /// table of common MyChron channels. Each is still verified against the table before use, so a
    /// wrong entry here degrades to an extra stream rather than to a silently unbound column.
    /// </summary>
    private static readonly (string Match, string Header, string Units)[] Renames =
    {
        ("logger temperature", "Logger Temperature", "°C"),   // degree sign required
        ("loggertemp",         "Logger Temperature", "°C"),
        ("internal batt",      "Battery",            "V"),    // "Internal Batt (V)" is not in the table
        ("internal battery",   "Battery",            "V"),
        ("battery",            "Battery",            "V"),
        ("water temp",         "Coolant Temperature", "C"),   // plain C here, no degree sign
        ("water temperature",  "Coolant Temperature", "C"),
        ("coolant temp",       "Coolant Temperature", "C"),
        ("oil temp",           "Oil Temperature",    "°C"),   // degree sign again
        ("oil temperature",    "Oil Temperature",    "°C"),
        ("oil press",          "Oil Pressure",       "bar"),
        ("oil pressure",       "Oil Pressure",       "bar"),
        ("brake press",        "Brake Pressure",     "bar"),
        ("brake pressure",     "Brake Pressure",     "bar"),
        ("throttle",           "Throttle Position",  "%"),
        ("tps",                "Throttle Position",  "%"),
        ("gear",               "Gear",               "#"),
        ("rpm",                "RPM",                "rpm"),
        ("speed",              "Speed",              "km/h"),
    };

    /// <summary>
    /// Decide what header and units a discovered channel is written under: its own name when that
    /// already matches, else a documented rename, else verbatim as an extra stream.
    /// </summary>
    public static ChannelHeader Resolve(string name, string units)
    {
        name = name.Trim();
        units = units.Trim();

        if (Contains(name, units)) return new ChannelHeader(name, units, ChannelBinding.Alias);

        // The units alone may be wrong (Logger Temperature exports "C", the table wants "°C").
        string lower = name.ToLowerInvariant();
        foreach (var (match, header, unit) in Renames)
        {
            if (lower != match) continue;
            if (Contains(header, unit)) return new ChannelHeader(header, unit, ChannelBinding.Renamed);
            break;
        }

        // Same channel under a slightly different spelling, e.g. "Water Temp 1".
        foreach (var (match, header, unit) in Renames)
        {
            if (!lower.StartsWith(match, StringComparison.Ordinal)) continue;
            if (Contains(header, unit)) return new ChannelHeader(header, unit, ChannelBinding.Renamed);
        }

        return new ChannelHeader(name, units, ChannelBinding.Extra);
    }

    private static HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TelemetryOverlayAliases.txt");
        if (stream is null) return set;

        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        bool started = false;
        while (reader.ReadLine() is string line)
        {
            // The file opens with an explanatory preamble; the entries follow a "=====" rule.
            if (!started) { started = line.StartsWith("====", StringComparison.Ordinal); continue; }
            if (line.Length > 0) set.Add(line.Trim());
        }
        return set;
    }
}
