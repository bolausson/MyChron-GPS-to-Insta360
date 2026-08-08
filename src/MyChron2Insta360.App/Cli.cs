using System.Globalization;
using System.Linq;
using MyChron2Insta360.Core;

namespace MyChron2Insta360.App;

/// <summary>What to write. Combinable, so --format gpx,telemetry-overlay works.</summary>
[Flags]
internal enum Outputs
{
    None = 0,
    Gpx = 1,
    Fit = 2,
    TelemetryOverlay = 4,
}

/// <summary>Headless command-line front-end. Any option flag routes here from <see cref="Program"/>.</summary>
internal static class Cli
{
    /// <summary>Parse --format: one or more of gpx, fit, both, telemetry-overlay, comma separated.</summary>
    internal static Outputs ParseFormats(string spec, out string? error)
    {
        error = null;
        var formats = Outputs.None;
        foreach (var raw in spec.Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "gpx": formats |= Outputs.Gpx; break;
                case "fit": formats |= Outputs.Fit; break;
                case "both": formats |= Outputs.Gpx | Outputs.Fit; break;
                case "telemetry-overlay": case "telemetryoverlay": case "to":
                    formats |= Outputs.TelemetryOverlay; break;
                default:
                    error = $"--format must be gpx, fit, both or telemetry-overlay (got '{raw.Trim()}').";
                    return Outputs.None;
            }
        }
        if (formats == Outputs.None) error = "--format needs at least one of gpx, fit, both, telemetry-overlay.";
        return formats;
    }

    public static int Run(string[] args)
    {
        // The log prints degree signs and arrows; without this they arrive as '?' on a legacy code page.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected or no console */ }

        try
        {
            if (args.Any(a => a is "-h" or "--help")) { PrintHelp(); return 0; }
            if (args.Any(a => a == "--version")) { Console.WriteLine($"MyChron2Insta360 {AppInfo.Version}"); return 0; }

            // A settings file, if there is one, supplies the starting values; any flag below overrides
            // it. Without one nothing changes, so existing scripts keep behaving exactly as before.
            string? iniPath = null;
            bool useIni = true;
            for (int k = 0; k < args.Length; k++)
            {
                if (args[k] == "--ini" && k + 1 < args.Length) iniPath = args[k + 1];
                else if (args[k] == "--no-ini") useIni = false;
            }
            var settings = new AppSettings { Format = "gpx" };   // the command line's own default
            string iniUsed = "";
            if (useIni && AppSettings.Exists(iniPath))
            {
                settings = AppSettings.Load(iniPath, out iniUsed, out string? iniError);
                if (iniError is not null) Console.Error.WriteLine($"Warning: could not read {iniUsed}: {iniError}");
                else Console.WriteLine($"Settings from {iniUsed}");
            }

            var inputs = new List<string>();
            string? output = null, dateStr = null;
            string? outDir = settings.OutDir.Length > 0 ? settings.OutDir : null;
            // A settings file saved with every output unticked would otherwise be an error here.
            string format = settings.Format.Trim().Length > 0 ? settings.Format : "gpx";
            double? nudge = settings.Nudge != 0 ? settings.Nudge : null;
            double? hz = settings.NativeRate ? null : settings.Hz;
            double? maxAcc = settings.MaxAccuracy != 0 ? settings.MaxAccuracy : null;
            int? leap = null;
            bool gps10 = false;
            bool trim = settings.Trim, merge = settings.Merge, writeImuCsv = settings.ImuCsv;
            double imuHz = settings.ImuHz; // 0 = native rate
            bool tilt = !settings.Tilt.Equals("off", StringComparison.OrdinalIgnoreCase);
            bool detectTilt = false;
            // null with tilt on = measure it from the data
            double? tiltDeg = tilt && !settings.Tilt.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? ParseD(settings.Tilt) : null;
            bool noXrk = !settings.PreferXrk, verifyXrk = false;
            double toRate = settings.ToRate;
            bool toInvertSteering = settings.ToInvertSteering;   // Telemetry Overlay renders steering mirrored
            bool toInvertLateral = settings.ToInvertLateral;     // off keeps the "seat of the pants" sense

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
                    case "--tilt":
                        // Optional value: "--tilt" or "--tilt auto" measures it, "--tilt 12.5" sets it.
                        tilt = true;
                        if (i + 1 < args.Length)
                        {
                            if (string.Equals(args[i + 1], "auto", StringComparison.OrdinalIgnoreCase)) i++;
                            else if (ParseD(args[i + 1]) is double td) { tiltDeg = td; i++; }
                        }
                        break;
                    case "--detect-tilt": detectTilt = true; break;
                    case "--xrk-dll": XrkReader.SetLibraryFolder(Next()); break;
                    case "--no-xrk": noXrk = true; break;
                    case "--verify-xrk": verifyXrk = true; break;
                    case "--to-rate": toRate = ParseD(Next()) ?? 50; break;
                    case "--to-invert-steering": toInvertSteering = true; break;
                    case "--no-to-invert-steering": toInvertSteering = false; break;
                    case "--to-invert-lateral": toInvertLateral = true; break;
                    case "--no-to-invert-lateral": toInvertLateral = false; break;
                    case "--ini": Next(); break;      // already read, before this loop
                    case "--no-ini": break;
                    case "--nogui": break;
                    default:
                        if (a.Length > 0 && a[0] != '-') inputs.Add(a);
                        break;
                }
            }

            if (inputs.Count == 0) { Console.Error.WriteLine("Error: no input (a RaceStudio _CSV folder, a parent folder, or _GPS*.csv).\n"); PrintHelp(); return 2; }
            foreach (var inp in inputs)
                if (!File.Exists(inp) && !Directory.Exists(inp)) { Console.Error.WriteLine($"Error: input not found: {inp}"); return 2; }
            var formats = ParseFormats(format, out string? formatError);
            if (formatError is not null) { Console.Error.WriteLine($"Error: {formatError}"); return 2; }

            if (verifyXrk) return VerifyXrk(inputs);

            if (detectTilt)
            {
                var found = TiltPlan.DetectFrom(inputs, out string src, out string? whyNot);
                if (found is null) { Console.Error.WriteLine($"Error: cannot measure the tilt: {whyNot}."); return 1; }
                Console.WriteLine($"Mount tilt measured from {src}:");
                foreach (var line in TiltPlan.Describe(found)) Console.WriteLine(line);
                return 0;
            }
            var tiltPlan = new TiltPlan { Enabled = tilt, ManualDeg = tiltDeg };

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

            // Insta360 video input (.insv/.lrv, or folders holding them) uses the native trailer parser.
            var clips = Insta360Trailer.DiscoverClips(inputs);
            if (clips.Count > 0)
            {
                // The Telemetry Overlay export merges a RaceStudio channel folder, and its axis mapping
                // is the MyChron frame, so it does not apply to camera telemetry.
                if (formats.HasFlag(Outputs.TelemetryOverlay))
                    Console.WriteLine("Note: the Telemetry Overlay CSV is a MyChron export; skipping it for Insta360 input.");
                return RunInsta360(clips, output, outDir, formats, writeImuCsv, imuHz, merge, tiltPlan);
            }

            var sources = MyChronSources.LoadAll(inputs, gps10, preferXrk: !noXrk);
            var sessions = sources.Select(s => s.Gps).ToList();
            Console.WriteLine($"Found {sources.Count} session(s):");
            foreach (var s in sources) Console.WriteLine("  " + s.Describe());
            if (outDir is not null) Directory.CreateDirectory(outDir);

            if (merge && sources.Count > 1)
            {
                Console.WriteLine("Mode: MERGE → one output file.");
                var segs = TrackBuilder.Build(sessions, opt);
                ReportBuild(opt);
                string primary = inputs[0];
                string baseName = MergedBaseName(opt);
                string dir = outDir ?? (Directory.Exists(primary) ? primary : Path.GetDirectoryName(sources[0].Gps.SourceFile)!);
                string name = $"{baseName} ({sources.Count} sessions)";
                string mergeBase = output ?? Path.Combine(dir, baseName);
                WriteJob(segs, mergeBase, name, formats);
                bool wantTo = formats.HasFlag(Outputs.TelemetryOverlay);
                if (writeImuCsv || wantTo)
                {
                    var all = new List<ImuSample>();
                    foreach (var src in sources)
                    {
                        var im = src.LoadImu();
                        tiltPlan.ApplyTo(im, Console.WriteLine);
                        if (imuHz > 0) im = ImuCsvWriter.Downsample(im, imuHz);
                        // The AiM container describes one session, so this export stays per session
                        // even when the GPX and FIT are merged.
                        if (wantTo)
                            WriteTelemetryOverlay(src, opt, im, Path.Combine(dir, src.BaseName),
                                                  toRate, toInvertSteering, toInvertLateral);
                        if (SessionStartUtc(src.Gps, opt) is DateTime su) foreach (var x in im) x.Utc = su.AddSeconds(x.ElapsedS);
                        all.AddRange(im);
                    }
                    if (writeImuCsv && all.Count > 0)
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
                if (sources.Count > 1) Console.WriteLine("Mode: individual → one output file per session (use --merge to combine).");
                foreach (var src in sources)
                {
                    var segs = TrackBuilder.Build(new[] { src.Gps }, opt);
                    string baseName = src.BaseName;
                    string dir = outDir ?? (src.FromXrk ? Path.GetDirectoryName(src.Path)! : src.Path);
                    string basePath = (output is not null && sources.Count == 1) ? output : Path.Combine(dir, baseName);
                    Console.WriteLine($"[{baseName}] {opt.TotalPoints} pts @ {opt.EffectiveHz:0.#} Hz" +
                                      (opt.StartUtc is DateTime t0 ? $"  {t0:HH:mm:ss}Z.." + (opt.EndUtc is DateTime t1 ? $"{t1:HH:mm:ss}Z" : "") : ""));
                    var extras = LoadMyChronExtras(src, opt, imuHz, tiltPlan, out var imu);
                    WriteJob(segs, basePath, src.Gps.Name, formats, extras);
                    if (extras.Laps is { Count: > 0 } lapList) Console.WriteLine($"  {lapList.Count} lap(s) from the logger.");
                    // Same levelled IMU stream the FIT and the IMU CSV get, never re-read from disk.
                    if (formats.HasFlag(Outputs.TelemetryOverlay))
                        WriteTelemetryOverlay(src, opt, imu, basePath, toRate, toInvertSteering, toInvertLateral);
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
    private static FitWriter.Extras LoadMyChronExtras(MyChronSource src, ConversionOptions opt, double imuHz,
                                                      TiltPlan tilt, out List<ImuSample> imu)
    {
        DateTime? startUtc = SessionStartUtc(src.Gps, opt);
        imu = src.LoadImu();
        tilt.ApplyTo(imu, Console.WriteLine);   // level before any downsample, so every output agrees
        if (imuHz > 0) imu = ImuCsvWriter.Downsample(imu, imuHz);
        if (startUtc is DateTime su)
            foreach (var x in imu) x.Utc = su.AddSeconds(x.ElapsedS);

        var laps = new List<LapSplit>();
        if (startUtc is DateTime lapStart)
            foreach (var (number, start, duration) in src.LoadLaps())
                laps.Add(new LapSplit { Number = number, StartUtc = lapStart.AddSeconds(start), DurationS = duration });

        return new FitWriter.Extras
        {
            Imu = imu.Count > 0 ? imu : null,
            Laps = laps,
            Temperature = MyChronSources.Temperature(src.LoadChannels(), startUtc),
        };
    }

    /// <summary>
    /// Read each .xrk three ways, our own parser, AiM's library and the RaceStudio CSV export, and
    /// report where they disagree. The CSV rounds on the way out, so it is compared with a tolerance;
    /// our parser and AiM's library should agree exactly.
    /// </summary>
    private static int VerifyXrk(List<string> inputs)
    {
        var files = MyChronSources.Discover(inputs).Where(c => c.IsXrk).Select(c => c.Path).ToList();
        if (files.Count == 0) { Console.Error.WriteLine("Error: no .xrk/.xrz found in the input(s)."); return 2; }

        bool allOk = true;
        foreach (var file in files)
        {
            Console.WriteLine($"\n{Path.GetFileName(file)}");
            var mine = XrkReader.Load(file);
            Console.WriteLine($"  ours : {mine.Gps.Samples.Count} GPS, {mine.Imu.Count} IMU, " +
                              $"{mine.Channels.Count} channel(s), {mine.Laps.Count} lap(s)  [{mine.Info.Describe()}]");
            if (mine.Info.TotalBytes > 0)
                Console.WriteLine($"         stepped over {mine.Info.UnreadBytes * 100.0 / mine.Info.TotalBytes:0.#}% of the file " +
                                  "(vendor blocks this tool has no use for)");

            XrkData? dll = null;
            if (XrkReader.DllAvailable)
            {
                dll = XrkReader.LoadViaDll(file);
                Console.WriteLine($"  AiM  : {dll.Gps.Samples.Count} GPS, {dll.Imu.Count} IMU, " +
                                  $"{dll.Channels.Count} channel(s), {dll.Laps.Count} lap(s)");
            }
            else Console.WriteLine($"  AiM  : library not present, skipping that comparison ({XrkReader.DllUnavailableReason})");

            string csvFolder = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "_CSV");
            var csv = Directory.Exists(csvFolder) ? RaceStudioChannelScan.Discover(csvFolder) : null;
            Console.WriteLine(csv is null ? "  CSV  : no _CSV export beside it, skipping that comparison"
                                          : $"  CSV  : {csv.Count} channel file(s) in {Path.GetFileName(csvFolder)}");

            if (dll is not null) allOk &= CompareToDll(mine, dll);
            if (csv is not null) CompareToCsv(mine, csv);
        }

        Console.WriteLine(allOk ? "\nOur parser matches AiM's library." : "\nDifferences found, see above.");
        return allOk ? 0 : 1;
    }

    private static bool CompareToDll(XrkData mine, XrkData dll)
    {
        bool ok = true;
        Console.WriteLine("\n  vs AiM's library (these should agree exactly):");
        Console.WriteLine($"    {"channel",-22} {"count",8} {"max |dt| s",14} {"max |dv|",14}");

        foreach (var a in mine.Channels)
        {
            var b = dll.Channels.FirstOrDefault(c => c.Name == a.Name);
            if (b is null)
            {
                // Not a mismatch: AiM's library hides some channels the logger really recorded.
                Console.WriteLine($"    {a.Name,-22} {a.Values.Count,8} {"",14} {"",14}  present here, not exposed by AiM");
                continue;
            }
            ok &= Row(a.Name, a.Times, a.Values, b.Times, b.Values);
        }
        ok &= Row("IMU accel X", mine.Imu.Select(s => s.ElapsedS).ToList(), mine.Imu.Select(s => s.Ax).ToList(),
                  dll.Imu.Select(s => s.ElapsedS).ToList(), dll.Imu.Select(s => s.Ax).ToList());
        ok &= Row("IMU gyro Z", mine.Imu.Select(s => s.ElapsedS).ToList(), mine.Imu.Select(s => s.Gz).ToList(),
                  dll.Imu.Select(s => s.ElapsedS).ToList(), dll.Imu.Select(s => s.Gz).ToList());

        var mg = mine.Gps.Samples; var dg = dll.Gps.Samples;
        ok &= Row("GPS latitude", mg.Select(s => s.TimeRel).ToList(), mg.Select(s => s.Lat).ToList(),
                  dg.Select(s => s.TimeRel).ToList(), dg.Select(s => s.Lat).ToList(), 1e-9);
        ok &= Row("GPS longitude", mg.Select(s => s.TimeRel).ToList(), mg.Select(s => s.Lon).ToList(),
                  dg.Select(s => s.TimeRel).ToList(), dg.Select(s => s.Lon).ToList(), 1e-9);
        ok &= Row("GPS altitude", mg.Select(s => s.TimeRel).ToList(), mg.Select(s => s.Alt).ToList(),
                  dg.Select(s => s.TimeRel).ToList(), dg.Select(s => s.Alt).ToList(), 1e-6);
        ok &= Row("GPS speed", mg.Select(s => s.TimeRel).ToList(), mg.Select(s => s.SpeedMs).ToList(),
                  dg.Select(s => s.TimeRel).ToList(), dg.Select(s => s.SpeedMs).ToList());
        ok &= Row("GPS iTOW", mg.Select(s => s.TimeRel).ToList(), mg.Select(s => s.ItowMs).ToList(),
                  dg.Select(s => s.TimeRel).ToList(), dg.Select(s => s.ItowMs).ToList());

        bool laps = mine.Laps.Count == dll.Laps.Count;
        double lapDiff = 0;
        for (int i = 0; i < Math.Min(mine.Laps.Count, dll.Laps.Count); i++)
            lapDiff = Math.Max(lapDiff, Math.Max(Math.Abs(mine.Laps[i].StartS - dll.Laps[i].StartS),
                                                 Math.Abs(mine.Laps[i].DurationS - dll.Laps[i].DurationS)));
        Console.WriteLine($"    {"laps",-22} {mine.Laps.Count,8} {"",14} {lapDiff,14:0.000000}  {(laps && lapDiff < 1e-6 ? "OK" : "CHECK")}");
        if (mine.Info.GpsWeek != dll.Info.GpsWeek)
        { Console.WriteLine($"    GPS week differs: {mine.Info.GpsWeek} vs {dll.Info.GpsWeek}"); ok = false; }
        return ok && laps && lapDiff < 1e-6;
    }

    private static bool Row(string label, List<double> at, List<double> av, List<double> bt, List<double> bv,
                            double tol = 1e-6)
    {
        int n = Math.Min(av.Count, bv.Count);
        double dt = 0, dv = 0;
        for (int i = 0; i < n; i++)
        {
            dt = Math.Max(dt, Math.Abs(at[i] - bt[i]));
            dv = Math.Max(dv, Math.Abs(av[i] - bv[i]));
        }
        bool ok = av.Count == bv.Count && dt < 1e-9 && dv <= tol;
        Console.WriteLine($"    {label,-22} {av.Count,8} {dt,14:0.000000000} {dv,14:0.000000000}  " +
                          (av.Count != bv.Count ? $"COUNT {av.Count} vs {bv.Count}" : ok ? "OK" : "CHECK"));
        return ok;
    }

    /// <summary>The CSV export rounds, so agreement is only expected to its own precision.</summary>
    private static void CompareToCsv(XrkData mine, List<ChannelSeries> csv)
    {
        Console.WriteLine("\n  vs the RaceStudio CSV export (that export rounds, so a small difference is expected):");
        foreach (var c in csv.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            List<double> times, values;
            if (RaceStudioChannelScan.IsImuChannel(c.Name))
            {
                times = mine.Imu.Select(s => s.ElapsedS).ToList();
                values = c.Name.ToLowerInvariant() switch
                {
                    "accelerometerx" => mine.Imu.Select(s => s.Ax).ToList(),
                    "accelerometery" => mine.Imu.Select(s => s.Ay).ToList(),
                    "accelerometerz" => mine.Imu.Select(s => s.Az).ToList(),
                    "gyrox" => mine.Imu.Select(s => s.Gx).ToList(),
                    "gyroy" => mine.Imu.Select(s => s.Gy).ToList(),
                    _ => mine.Imu.Select(s => s.Gz).ToList(),
                };
            }
            else
            {
                var mineCh = mine.Channels.FirstOrDefault(x => x.Name == c.Name);
                if (mineCh is null) { Console.WriteLine($"    {c.Name,-22} not produced by our parser"); continue; }
                times = mineCh.Times; values = mineCh.Values;
            }

            // Match on timestamp rather than position: the IMU stream carries the gyro re-timed onto
            // the accelerometer grid, so the two lists are not index-for-index comparable.
            double dt = 0, dv = 0;
            int j = 0;
            for (int i = 0; i < values.Count && c.Times.Count > 0; i++)
            {
                while (j + 1 < c.Times.Count && Math.Abs(c.Times[j + 1] - times[i]) <= Math.Abs(c.Times[j] - times[i])) j++;
                dt = Math.Max(dt, Math.Abs(times[i] - c.Times[j]));
                dv = Math.Max(dv, Math.Abs(values[i] - c.Values[j]));
            }
            string verdict = dv <= 0.0500001 ? "within the CSV's rounding" : "CHECK";
            string counts = values.Count == c.Values.Count ? $"{values.Count}" : $"{values.Count}/{c.Values.Count}";
            Console.WriteLine($"    {c.Name,-22} {counts,12} {dt,14:0.000000000} {dv,14:0.000000000}  {verdict}");
        }
    }

    /// <summary>
    /// Merge one session into a single wide CSV for Telemetry Overlay. The IMU comes in already
    /// levelled from <see cref="LoadMyChronExtras"/>; the other channels are discovered in the folder,
    /// whatever they happen to be.
    /// </summary>
    private static void WriteTelemetryOverlay(MyChronSource src, ConversionOptions opt, IReadOnlyList<ImuSample> imu,
                                              string basePath, double rate, bool invertSteering, bool invertLateral)
    {
        var channels = src.LoadChannels();
        var laps = src.LoadLaps();
        var toOpt = new TelemetryOverlayOptions
        {
            RateHz = rate,
            InvertSteering = invertSteering,
            InvertLateral = invertLateral,
            // An xrk knows the track, racer and championship; a CSV export does not.
            SessionName = src.Info?.Track is { Length: > 0 } track ? track : src.Gps.Name,
            Racer = src.Info?.Racer ?? "",
            Vehicle = src.Info?.Vehicle ?? "",
            Championship = src.Info?.Championship ?? "",
            StartLocal = SessionStartUtc(src.Gps, opt) is DateTime utc
                ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local)
                : src.Info?.StartLocal,
        };

        string path = basePath + "_TelemetryOverlay.csv";
        Console.WriteLine($"  Telemetry Overlay CSV, {channels.Count} channel(s) found:");
        TelemetryOverlayCsv.Write(path, src.Gps, imu, channels, laps, toOpt, Console.WriteLine);
        Console.WriteLine($"Wrote {path}");
    }

    private static DateTime? SessionStartUtc(GpsSession s, ConversionOptions opt)
    {
        // An xrk carries the GPS week, which fixes the instant without any calendar hint.
        int? week = opt.DateOverrideUtc is null ? s.GpsWeek : null;
        DateTime? dateHint = opt.DateOverrideUtc ?? s.DateHint;
        if (week is null && dateHint is null) return null;
        foreach (var g in s.Samples)
            if (!double.IsNaN(g.ItowMs) && g.ItowMs > 0)
                return (week is int w ? ItowTime.FromWeek(w, g.ItowMs, opt.LeapSeconds)
                                      : ItowTime.ToUtc(g.ItowMs, dateHint!.Value, opt.LeapSeconds))
                       .AddSeconds(-g.TimeRel);
        return null;
    }

    private static int RunInsta360(List<Insta360Clip> clips, string? output, string? outDir, Outputs formats,
                                   bool writeImuCsv, double imuHz, bool merge, TiltPlan tilt)
    {
        Console.WriteLine($"Found {clips.Count} Insta360 clip(s).");
        if (outDir is not null) Directory.CreateDirectory(outDir);

        // Read the clips in parallel: they are independent, and parsing plus encoding is CPU-bound.
        // Parallelism is capped because each clip holds its whole IMU stream in memory.
        var slots = new (Insta360Clip Clip, Insta360Telemetry Tel, string File)?[clips.Count];
        Parallel.For(0, clips.Count, new ParallelOptions { MaxDegreeOfParallelism = Parallelism(clips.Count) }, i =>
        {
            var tel = Insta360Trailer.ParseClip(clips[i], out string used);
            if (tel.Gps.Count > 0)
            {
                DateTime g0 = tel.Gps[0].Utc; // anchor the IMU to absolute UTC via the first fix
                foreach (var s in tel.Imu) s.Utc = g0.AddSeconds(s.ElapsedS);
            }
            slots[i] = (clips[i], tel, used);
        });

        var loaded = new List<(Insta360Clip Clip, Insta360Telemetry Tel, string File)>();
        foreach (var slot in slots)
        {
            if (slot is not { } l) continue;
            Console.WriteLine($"  {Path.GetFileName(l.File)}: {l.Tel.Gps.Count} GPS points, {l.Tel.Imu.Count} IMU samples" +
                              (l.Tel.Gps.Count > 0 ? $"  {l.Tel.Gps[0].Utc:HH:mm:ss}Z..{l.Tel.Gps[^1].Utc:HH:mm:ss}Z" : ""));
            if (l.Tel.Gps.Count == 0 && l.Tel.Imu.Count == 0)
            { Console.WriteLine("    no telemetry (was a GPS remote or the phone app connected while recording?)"); continue; }
            loaded.Add(l);
        }
        if (loaded.Count == 0) { Console.Error.WriteLine("No telemetry found in any clip."); return 1; }

        loaded.Sort((a, b) => (a.Tel.Gps.Count > 0 ? a.Tel.Gps[0].Utc : DateTime.MaxValue)
                       .CompareTo(b.Tel.Gps.Count > 0 ? b.Tel.Gps[0].Utc : DateTime.MaxValue));
        ReportClipGaps(loaded.Select(l => l.Tel).ToList());
        foreach (var l in loaded) tilt.ApplyTo(l.Tel.Imu, Console.WriteLine);

        if (merge && loaded.Count > 1)
        {
            Console.WriteLine("Mode: MERGE, one output file.");
            var segs = loaded.Where(l => l.Tel.Gps.Count > 0).Select(l => l.Tel.Gps).ToList();
            var imu = new List<ImuSample>();
            foreach (var l in loaded) imu.AddRange(l.Tel.Imu);
            imu.Sort((a, b) => (a.Utc ?? DateTime.MinValue).CompareTo(b.Utc ?? DateTime.MinValue));
            if (imuHz > 0) imu = DownsampleByUtc(imu, imuHz);

            DateTime? start = segs.Count > 0 ? segs[0][0].Utc : imu.FirstOrDefault()?.Utc;
            string baseName = "merged-" + (start is DateTime st ? st.ToString("yyyyMMdd-HHmmss") : "unknown");
            string dir = outDir ?? Path.GetDirectoryName(loaded[0].File)!;
            string basePath = output ?? Path.Combine(dir, baseName);
            string name = $"{baseName} ({loaded.Count} clips)";

            if (segs.Count > 0)
            {
                int total = segs.Sum(s => s.Count);
                Console.WriteLine($"  merged {total} GPS points, UTC {segs[0][0].Utc:yyyy-MM-dd HH:mm:ss}Z .. {segs[^1][^1].Utc:HH:mm:ss}Z");
                WriteJob(segs, basePath, name, formats, new FitWriter.Extras { Imu = imu.Count > 0 ? imu : null });
            }
            if (writeImuCsv && imu.Count > 0) WriteImuCsv(basePath, imu);
        }
        else
        {
            if (loaded.Count > 1) Console.WriteLine("Mode: individual, one output set per clip (use --merge to combine).");
            // Clips are independent, so encode and write them in parallel; messages are buffered per
            // clip and printed in order afterwards so the log stays readable.
            var logs = new List<string>[loaded.Count];
            Parallel.For(0, loaded.Count, new ParallelOptions { MaxDegreeOfParallelism = Parallelism(loaded.Count) }, i =>
            {
                var (clip, tel, used) = loaded[i];
                var log = logs[i] = new List<string>();
                string dir = outDir ?? Path.GetDirectoryName(used)!;
                string baseName = Path.GetFileNameWithoutExtension(used);
                string basePath = (output is not null && loaded.Count == 1) ? output : Path.Combine(dir, baseName);
                var imu = imuHz > 0 ? ImuCsvWriter.Downsample(tel.Imu, imuHz) : tel.Imu;

                if (tel.Gps.Count > 0)
                {
                    var extras = new FitWriter.Extras { Imu = imu.Count > 0 ? imu : null };
                    foreach (var (p, isFit) in FormatPaths(basePath, formats))
                    {
                        if (isFit) FitWriter.Write(p, new List<List<TrackPoint>> { tel.Gps }, baseName, extras);
                        else GpxWriter.Write(p, new List<List<TrackPoint>> { tel.Gps }, baseName);
                        log.Add($"Wrote {p}");
                    }
                }
                else log.Add($"  [{baseName}] no GPS, skipping GPX/FIT");

                if (writeImuCsv && imu.Count > 0)
                {
                    string csv = basePath + "_imu.csv";
                    ImuCsvWriter.Write(csv, imu);
                    log.Add($"Wrote {csv}  ({imu.Count} IMU samples)");
                }
            });
            foreach (var log in logs)
                if (log is not null) foreach (var line in log) Console.WriteLine(line);
        }
        return 0;
    }

    /// <summary>
    /// How many clips to process at once. Capped well below the core count because each clip holds its
    /// entire IMU stream in memory (roughly 100 MB for a 17 minute clip at 1 kHz).
    /// </summary>
    private static int Parallelism(int items) => Math.Max(1, Math.Min(items, Math.Min(4, Environment.ProcessorCount)));

    private static void WriteImuCsv(string basePath, List<ImuSample> imu)
    {
        string csv = basePath + "_imu.csv";
        ImuCsvWriter.Write(csv, imu);
        Console.WriteLine($"Wrote {csv}  ({imu.Count} IMU samples)");
    }

    /// <summary>Downsample on absolute time, needed once clips are concatenated onto one timeline.</summary>
    private static List<ImuSample> DownsampleByUtc(List<ImuSample> imu, double hz)
    {
        var outp = new List<ImuSample>();
        double min = 1.0 / hz;
        DateTime? last = null;
        foreach (var s in imu)
        {
            if (s.Utc is not DateTime u) continue;
            if (last is DateTime l && (u - l).TotalSeconds < min - 1e-9) continue;
            last = u;
            outp.Add(s);
        }
        return outp;
    }

    /// <summary>Show the gap between consecutive clips, which reveals a split recording versus separate runs.</summary>
    private static void ReportClipGaps(List<Insta360Telemetry> tels)
    {
        var withGps = tels.Where(t => t.Gps.Count > 0).ToList();
        for (int i = 1; i < withGps.Count; i++)
        {
            double gap = (withGps[i].Gps[0].Utc - withGps[i - 1].Gps[^1].Utc).TotalSeconds;
            string how = gap <= 5 ? "continuous, one split recording" : "separate recordings";
            Console.WriteLine($"  gap before clip {i + 1}: {gap:0.#} s ({how})");
        }
    }

    private static void ReportBuild(ConversionOptions opt)
    {
        Console.WriteLine($"  {opt.TotalPoints} points from {opt.SessionCount} session(s).");
        Console.WriteLine($"  Time base: {(opt.UsedItow ? "iTOW (exact GPS time)" : "relative (no iTOW, sync will need a nudge)")}");
        Console.WriteLine($"  Output rate: {(opt.TargetHz > 0 ? $"{opt.EffectiveHz:0.#} Hz (downsampled)" : $"{opt.EffectiveHz:0.#} Hz (native)")}");
        if (opt.StartUtc is DateTime a0 && opt.EndUtc is DateTime a1)
            Console.WriteLine($"  UTC span: {a0:yyyy-MM-dd HH:mm:ss}Z .. {a1:HH:mm:ss}Z");
    }

    private static void WriteJob(List<List<TrackPoint>> segments, string basePath, string trackName, Outputs formats,
                                 FitWriter.Extras? extras = null)
    {
        foreach (var (path, isFit) in FormatPaths(basePath, formats))
        {
            if (isFit) FitWriter.Write(path, segments, trackName, extras);
            else GpxWriter.Write(path, segments, trackName);
            Console.WriteLine($"Wrote {path}");
        }
    }

    private static IEnumerable<(string path, bool isFit)> FormatPaths(string basePath, Outputs formats)
    {
        if (formats.HasFlag(Outputs.Gpx)) yield return (Ext(basePath, ".gpx"), false);
        if (formats.HasFlag(Outputs.Fit)) yield return (Ext(basePath, ".fit"), true);
    }

    private static string DirName(string path) =>
        new DirectoryInfo(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

    /// <summary>Merged output base name: "merged-YYYYMMDD-HHMMSS" from the earliest GPS fix (UTC).</summary>
    internal static string MergedBaseName(ConversionOptions opt) =>
        "merged-" + (opt.StartUtc is DateTime st ? st.ToString("yyyyMMdd-HHmmss") : "unknown");

    private static string Ext(string p, string ext) => Path.HasExtension(p) ? Path.ChangeExtension(p, ext) : p + ext;

    /// <summary>
    /// Accept both "12.5" and "12,5": the log prints dots, but a German keyboard types commas. Float
    /// rather than Any is deliberate, because Any allows group separators and would read "20,0" as 200.
    /// </summary>
    internal static double? ParseD(string? s)
    {
        const NumberStyles Style = NumberStyles.Float;
        if (double.TryParse(s, Style, CultureInfo.InvariantCulture, out var v)) return v;
        if (double.TryParse(s, Style, CultureInfo.CurrentCulture, out v)) return v;
        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
$@"MyChron2Insta360 {AppInfo.Version} - convert RaceStudio3 GPS CSV exports to GPX/FIT for the Insta360 Stats Dashboard.

Inputs (auto-detected):
  * A MyChron '.xrk' or '.xrz' logger file, read directly, no CSV export needed and nothing to
    install. Use --verify-xrk to check that reader against AiM's own library and the CSV export.
  * RaceStudio '..._CSV' folder(s), a PARENT folder holding several, or a _GPS_o.csv / _GPS.csv.
    Each session becomes its own output file; --merge combines them onto one timeline via GPS 'itow'.
  * An Insta360 video (.insv / .lrv) recorded with a GPS remote or the phone app connected.

What each output holds:
  GPX  GPS track only: position, altitude, per-point speed and time. Best for maps and Insta360.
  FIT  GPS track PLUS accelerometer and gyroscope, temperature and lap splits (where available).
  CSV  Accelerometer (g) and gyroscope (deg/s) only, one row per sample, for analysis in other apps.
  TO   One wide CSV for Telemetry Overlay (goprotelemetryextractor.com): GPS, IMU, laps and every
       other channel the logger recorded, merged onto one time base.

Usage:
  MyChron2Insta360 <folder> [more folders...] [options]   CLI mode (any option triggers CLI)
  MyChron2Insta360                                          no args -> GUI
  MyChron2Insta360 <folder>                                 drag onto the .exe -> GUI, preloaded

Options:
  --merge                   Merge all sessions into ONE output (default: one file per session).
  -o, --out <file>          Explicit output path (single-output only; extension set per format).
  --out-dir <folder>        Write outputs into this folder (created if needed).
  --format <list>           Output format(s), comma separated: gpx, fit, both, telemetry-overlay.
                            Default gpx. (--fit / --both are shortcuts.)
  --gps10                   Use _GPS.csv (10 Hz) instead of _GPS_o.csv (25 Hz). CSV exports only.
  --no-xrk                  Ignore .xrk/.xrz and use the RaceStudio CSV export instead.
  --ini <file>              Read starting values from this settings file instead of the default
                            (MyChron2Insta360.ini beside this program). Any option here wins over it.
  --no-ini                  Ignore the settings file entirely.
  --verify-xrk              Read an .xrk with this tool, with AiM's library and from the CSV export,
                            report every difference, then exit. Writes nothing.
  --xrk-dll <folder>        Where AiM's library sits. Only --verify-xrk uses it.
  --hz <n>                  Downsample to n Hz. Default: keep the native rate.
  --nudge <seconds>         Add seconds to every timestamp (fine sync tuning).
  --max-accuracy <m>        Drop fixes with reported accuracy worse than m metres (default: keep all).
  --date <YYYY-MM-DD>       Override the session date (only if it isn't in the folder path).
  --leap <n>                GPS-UTC leap seconds for iTOW (default {ItowTime.DefaultLeapSeconds}).
  --trim                    Trim leading/trailing standstill per session (off by default).
  --no-imu-csv              Do not write the separate accel/gyro CSV (it is written by default).
  --imu-hz <n>              Downsample the IMU to n Hz (default: native, ~1000 Hz on Insta360).
  --tilt [deg]              Correct the mount tilt: rotate the IMU so gravity sits on the vertical
                            axis. Without a value (or with 'auto') the angle is measured from the
                            data; give a number to use a known angle instead.
  --detect-tilt             Only measure and print the mount tilt, then exit. Writes nothing.

 Telemetry Overlay CSV (--format telemetry-overlay), MyChron sessions only:
  --to-rate <hz>            Output grid rate (default 50, the native accel/gyro rate).
  --no-to-invert-steering   Keep the logged steering sign (default: negated, which is what
                            Telemetry Overlay's steering gauge expects).
  --to-invert-lateral       Negate lateral g (default: off, the 'seat of the pants' sense).
  -h, --help                Show this help.
  --version                 Show version.

Import the output in the Insta360 app: Dashboard -> Data Source -> Local Files -> Import.");
    }
}
