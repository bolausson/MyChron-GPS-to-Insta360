namespace MyChron2Insta360.Core;

/// <summary>
/// One MyChron session, however it was found: an .xrk / .xrz logger file read through AiM's library, or
/// a RaceStudio "_CSV" export folder. Everything downstream works from this, so the two sources produce
/// identical output apart from precision.
/// </summary>
public sealed class MyChronSource
{
    public required GpsSession Gps { get; init; }
    /// <summary>The xrk/xrz file, or the "_CSV" folder.</summary>
    public required string Path { get; init; }
    /// <summary>Base name for the output files.</summary>
    public required string BaseName { get; init; }
    public bool FromXrk { get; init; }
    public XrkInfo? Info { get; init; }

    public required Func<List<ImuSample>> LoadImu { get; init; }
    public required Func<List<ChannelSeries>> LoadChannels { get; init; }
    public required Func<List<(int Number, double StartS, double DurationS)>> LoadLaps { get; init; }

    public string Describe() =>
        $"{BaseName}  ({Gps.Samples.Count} fixes, {Gps.NativeHz:0.#} Hz, {(FromXrk ? "xrk" : "CSV")})" +
        (Info?.Describe() is { Length: > 0 } d ? $"  [{d}]" : "");
}

/// <summary>
/// Finds MyChron sessions under the given inputs. An .xrk holds everything a "_CSV" folder holds, at
/// full precision and in one file, so when both are present for the same session the xrk wins.
/// Pass preferXrk: false to force the CSV export instead.
/// </summary>
public static class MyChronSources
{
    /// <summary>One session that was found, before anything is parsed.</summary>
    public readonly record struct Candidate(string Path, bool IsXrk, string BaseName);

    /// <summary>Parse the chosen sessions. See <see cref="Discover"/> for how they are picked.</summary>
    public static List<MyChronSource> LoadAll(IEnumerable<string> inputs, bool gps10 = false, bool preferXrk = true)
    {
        var sources = new List<MyChronSource>();
        foreach (var c in Discover(inputs, gps10, preferXrk))
        {
            if (!c.IsXrk) { sources.Add(FromCsvFolder(c.Path)); continue; }
            try { sources.Add(FromXrkFile(c.Path)); }
            catch (Exception) when (CsvBeside(c.Path) is not null)
            {
                // A logger file we cannot read should not lose the session when RaceStudio has
                // already exported it beside the file.
                sources.Add(FromCsvFolder(CsvBeside(c.Path)!));
            }
        }
        return sources;
    }

    /// <summary>
    /// Find the sessions without reading them, which is what the GUI needs to show a count while the
    /// user edits the list: an xrk is tens of megabytes and parsing one per keystroke would stutter.
    /// </summary>
    public static List<Candidate> Discover(IEnumerable<string> inputs, bool gps10 = false, bool preferXrk = true)
    {
        var xrkFiles = new List<string>();
        var gpsCsvFiles = new List<string>();
        FileNotFoundException? csvError = null;

        foreach (var input in inputs)
        {
            foreach (var x in DiscoverXrk(input))
                if (!xrkFiles.Contains(x, StringComparer.OrdinalIgnoreCase)) xrkFiles.Add(x);

            try
            {
                foreach (var f in RaceStudioGpsCsv.DiscoverGpsFiles(input, gps10))
                    if (!gpsCsvFiles.Contains(f, StringComparer.OrdinalIgnoreCase)) gpsCsvFiles.Add(f);
            }
            catch (FileNotFoundException e) { csvError ??= e; }
        }

        // The same session usually appears as X.xrk, X.xrz and X_CSV, so keep one of each set.
        bool useXrk = preferXrk && XrkReader.Available;
        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        if (useXrk)
            foreach (var x in xrkFiles.OrderBy(p => System.IO.Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            {
                string key = SessionKey(x);
                if (chosen.TryAdd(key, x)) order.Add(key);
            }

        foreach (var f in gpsCsvFiles)
        {
            string key = SessionKey(f);
            if (chosen.TryAdd(key, f)) order.Add(key);
        }

        if (chosen.Count == 0)
            throw csvError ?? new FileNotFoundException("No MyChron session found (no .xrk/.xrz and no _CSV export).");

        var found = new List<Candidate>();
        foreach (var key in order)
        {
            string path = chosen[key];
            bool isXrk = XrkReader.IsXrkFile(path);
            found.Add(new Candidate(path, isXrk,
                isXrk ? System.IO.Path.GetFileNameWithoutExtension(path)
                      : new DirectoryInfo(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!).Name));
        }
        return found;
    }

    /// <summary>The GPS csv of the "_CSV" export that belongs to a logger file, when RaceStudio made one.</summary>
    private static string? CsvBeside(string xrkPath)
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(xrkPath))!,
                                               System.IO.Path.GetFileNameWithoutExtension(xrkPath) + "_CSV");
        foreach (var name in RaceStudioGpsCsv.GpsFileNames)
        {
            string p = System.IO.Path.Combine(folder, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Groups X.xrk, X.xrz and X_CSV\ together: they are the same session in three wrappers.</summary>
    private static string SessionKey(string path)
    {
        if (XrkReader.IsXrkFile(path)) return System.IO.Path.GetFileNameWithoutExtension(path);
        string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? path;
        string name = new DirectoryInfo(folder).Name;
        return name.EndsWith("_CSV", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static IEnumerable<string> DiscoverXrk(string input)
    {
        if (File.Exists(input))
        {
            if (XrkReader.IsXrkFile(input)) yield return System.IO.Path.GetFullPath(input);
            yield break;
        }
        if (!Directory.Exists(input)) yield break;

        foreach (var ext in XrkReader.Extensions)
            foreach (var f in Directory.EnumerateFiles(input, "*" + ext, SearchOption.AllDirectories)
                                       .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                yield return f;
    }

    private static MyChronSource FromXrkFile(string path)
    {
        var data = XrkReader.Load(path);
        return new MyChronSource
        {
            Gps = data.Gps,
            Path = path,
            BaseName = System.IO.Path.GetFileNameWithoutExtension(path),
            FromXrk = true,
            Info = data.Info,
            LoadImu = () => data.Imu,
            LoadChannels = () => data.Channels,
            LoadLaps = () => data.Laps,
        };
    }

    private static MyChronSource FromCsvFolder(string gpsCsv)
    {
        var session = RaceStudioGpsCsv.Parse(gpsCsv);
        session.DateHint = RaceStudioGpsCsv.ExtractDateHint(gpsCsv);
        session.Name = RaceStudioGpsCsv.ExtractName(gpsCsv);
        string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(gpsCsv))!;
        return new MyChronSource
        {
            Gps = session,
            Path = folder,
            BaseName = new DirectoryInfo(folder).Name,
            FromXrk = false,
            LoadImu = () => RaceStudioImu.Load(folder),
            LoadChannels = () => RaceStudioChannelScan.Discover(folder),
            LoadLaps = () => RaceStudioChannels.LoadLapsElapsed(folder),
        };
    }

    /// <summary>Logger temperature pulled from whichever channel set the source provided.</summary>
    public static List<(DateTime Utc, double C)> Temperature(IEnumerable<ChannelSeries> channels, DateTime? startUtc)
    {
        var list = new List<(DateTime, double)>();
        if (startUtc is not DateTime start) return list;
        var t = channels.FirstOrDefault(c => c.Name.Replace(" ", "").Equals("LoggerTemperature", StringComparison.OrdinalIgnoreCase));
        if (t is null) return list;
        for (int i = 0; i < t.Values.Count; i++) list.Add((start.AddSeconds(t.Times[i]), t.Values[i]));
        return list;
    }
}
