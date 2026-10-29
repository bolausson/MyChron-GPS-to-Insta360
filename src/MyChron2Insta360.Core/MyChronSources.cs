namespace MyChron2Insta360.Core;

/// <summary>
/// One GPS session, however it was found: a MyChron .xrk / .xrz logger file, a RaceStudio "_CSV" export
/// folder, or a .vbo (the VBOX format the Dragy Lap app exports, GPS only). Everything downstream works
/// from this, so the sources produce the same outputs, each with whatever streams it carries.
/// </summary>
public sealed class MyChronSource
{
    public required GpsSession Gps { get; init; }
    /// <summary>The xrk/xrz or .vbo file, or the "_CSV" folder.</summary>
    public required string Path { get; init; }
    /// <summary>Base name for the output files.</summary>
    public required string BaseName { get; init; }
    public bool FromXrk { get; init; }
    /// <summary>Read from a .vbo: GPS only, with no IMU, other channels or laps.</summary>
    public bool FromVbo { get; init; }
    public XrkInfo? Info { get; init; }
    public VboInfo? Vbo { get; init; }

    public required Func<List<ImuSample>> LoadImu { get; init; }
    public required Func<List<ChannelSeries>> LoadChannels { get; init; }
    public required Func<List<(int Number, double StartS, double DurationS)>> LoadLaps { get; init; }

    /// <summary>What it was read from: the file's own extension, or "CSV" for an export.</summary>
    public string SourceKind =>
        FromXrk || FromVbo ? System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant() : "CSV";

    /// <summary>Where the outputs go unless an output folder is set: beside a file, inside an export folder.</summary>
    public string OutputFolder =>
        FromXrk || FromVbo ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))! : Path;

    public string Describe() =>
        $"{BaseName}  ({Gps.Samples.Count} fixes, {Gps.NativeHz:0.#} Hz, {SourceKind})" +
        ((Info?.Describe() ?? Vbo?.Describe()) is { Length: > 0 } d ? $"  [{d}]" : "");
}

/// <summary>
/// Finds the sessions under the given inputs. An .xrk holds everything a "_CSV" folder holds, at full
/// precision and in one file, so when both are present for the same session the xrk wins. Pass
/// preferXrk: false to force the CSV export instead. Every .vbo is a session of its own.
/// </summary>
public static class MyChronSources
{
    /// <summary>One session that was found, before anything is parsed.</summary>
    public readonly record struct Candidate(string Path, bool IsXrk, string BaseName, bool IsVbo = false)
    {
        /// <summary>"xrk", "xrz", "vbo" or "CSV", so the log names the file actually used.</summary>
        public string Kind => IsXrk || IsVbo ? System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant() : "CSV";
    }

    /// <summary>Parse the chosen sessions. See <see cref="Discover"/> for how they are picked.</summary>
    /// <param name="vboDateUtc">The UTC date for a .vbo whose header gives none (--date).</param>
    /// <param name="log">Told about each .vbo that is skipped.</param>
    public static List<MyChronSource> LoadAll(IEnumerable<string> inputs, bool gps10 = false, bool preferXrk = true,
                                              DateTime? vboDateUtc = null, Action<string>? log = null)
    {
        var sources = new List<MyChronSource>();
        var found = Discover(inputs, gps10, preferXrk);
        Exception? skipped = null;
        foreach (var c in found)
        {
            if (c.IsVbo)
            {
                // One .vbo that cannot be read, or has no date, should not cost the rest of the run.
                try { sources.Add(FromVboFile(c.Path, vboDateUtc)); }
                catch (Exception e) when (found.Count > 1 && e is InvalidDataException or IOException)
                {
                    skipped = e;
                    log?.Invoke($"Note: skipping '{System.IO.Path.GetFileName(c.Path)}': {e.Message}");
                }
                continue;
            }
            if (!c.IsXrk) { sources.Add(FromCsvFolder(c.Path)); continue; }
            try { sources.Add(FromXrkFile(c.Path)); }
            catch (Exception) when (CsvBeside(c.Path) is not null)
            {
                // A logger file we cannot read should not lose the session when RaceStudio has
                // already exported it beside the file.
                sources.Add(FromCsvFolder(CsvBeside(c.Path)!));
            }
        }
        // Nothing left at all is still an error, not an empty run that reports success.
        if (sources.Count == 0 && skipped is not null) throw skipped;
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
        var vboFiles = new List<string>();
        var dlapFiles = new List<string>();
        FileNotFoundException? csvError = null;

        foreach (var input in inputs)
        {
            // A .vbo is a session of its own wherever it sits, even inside a "_CSV" folder, so it never
            // reaches the CSV discovery, which would take any file as standing in for its folder.
            if (File.Exists(input) && (VboReader.IsVboFile(input) || VboReader.IsDlapFile(input)))
            {
                AddOnce(VboReader.IsVboFile(input) ? vboFiles : dlapFiles, System.IO.Path.GetFullPath(input));
                continue;
            }
            if (Directory.Exists(input))
            {
                foreach (var v in VboReader.FindIn(input)) AddOnce(vboFiles, System.IO.Path.GetFullPath(v));
                foreach (var d in VboReader.FindDlapIn(input)) AddOnce(dlapFiles, System.IO.Path.GetFullPath(d));
            }

            foreach (var x in DiscoverXrk(input)) AddOnce(xrkFiles, x);

            // A folder holding only .vbo files has no export in it, which is fine; the reason is kept in
            // case nothing at all turns up, as it says more than a generic "not found".
            try { foreach (var f in RaceStudioGpsCsv.DiscoverGpsFiles(input, gps10)) AddOnce(gpsCsvFiles, f); }
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

        if (chosen.Count == 0 && vboFiles.Count == 0)
        {
            // A .dlap is only worth mentioning when it is all there was: it is why nothing was found.
            if (dlapFiles.Count > 0) throw new FileNotFoundException(VboReader.DlapMessage(dlapFiles[0]));
            throw csvError ?? new FileNotFoundException(
                "No session found: no .xrk/.xrz logger file, no RaceStudio _CSV export and no .vbo.");
        }

        var found = new List<Candidate>();
        foreach (var key in order)
        {
            string path = chosen[key];
            bool isXrk = XrkReader.IsXrkFile(path);
            found.Add(new Candidate(path, isXrk,
                isXrk ? System.IO.Path.GetFileNameWithoutExtension(path)
                      : new DirectoryInfo(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!).Name));
        }
        foreach (var v in vboFiles)
            found.Add(new Candidate(v, false, System.IO.Path.GetFileNameWithoutExtension(v), IsVbo: true));
        return found;
    }

    private static void AddOnce(List<string> list, string path)
    {
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
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
        RaceStudioGpsCsv.SetDateHint(session, gpsCsv);
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

    private static MyChronSource FromVboFile(string path, DateTime? dateUtc)
    {
        var data = VboReader.Load(path, dateUtc);
        return new MyChronSource
        {
            Gps = data.Gps,
            Path = path,
            BaseName = System.IO.Path.GetFileNameWithoutExtension(path),
            FromVbo = true,
            Vbo = data.Info,
            LoadImu = () => new List<ImuSample>(),
            LoadChannels = () => new List<ChannelSeries>(),
            LoadLaps = () => new List<(int Number, double StartS, double DurationS)>(),
        };
    }

    /// <summary>Logger temperature pulled from whichever channel set the source provided.</summary>
    public static List<(DateTime Utc, double C)> Temperature(IEnumerable<ChannelSeries> channels, DateTime? startUtc)
    {
        var list = new List<(DateTime, double)>();
        if (startUtc is not DateTime start) return list;
        var t = channels.FirstOrDefault(c => c.Name.Replace(" ", "").Equals("LoggerTemperature", StringComparison.OrdinalIgnoreCase));
        if (t is null) return list;
        for (int i = 0; i < t.Values.Count; i++) list.Add((start.PlusSeconds(t.Times[i]), t.Values[i]));
        return list;
    }
}
