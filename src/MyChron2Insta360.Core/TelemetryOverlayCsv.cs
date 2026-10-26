using System.Globalization;
using System.Text;

namespace MyChron2Insta360.Core;

public sealed class TelemetryOverlayOptions
{
    /// <summary>Output grid rate. 50 Hz matches the native accel/gyro/steering rate.</summary>
    public double RateHz = 50;

    /// <summary>Telemetry Overlay's steering gauge renders mirrored, so negate by default.</summary>
    public bool InvertSteering = true;

    /// <summary>Off keeps lateral g in the "seat of the pants" sense, which most people expect.</summary>
    public bool InvertLateral = false;

    public string SessionName = "";
    /// <summary>Preamble fields. An xrk supplies these; a CSV export does not.</summary>
    public string Racer = "", Vehicle = "", Championship = "";
    public DateTime? StartLocal;
}

/// <summary>
/// Writes one wide CSV in AiM "RS2Analysis" style for Telemetry Overlay
/// (goprotelemetryextractor.com), merging a session's streams into a single file: everything a MyChron
/// logged, or the GPS of a .vbo.
///
/// Every column is named so that "header (units)" hits Telemetry Overlay's built-in alias table, which
/// is what binds a column to a gauge. No remapheaders.txt is written: re-declaring names the app
/// already knows makes the whole file fail to load with only a generic error.
///
/// The IMU arrives as an already levelled <see cref="ImuSample"/> stream rather than being re-read
/// from the raw channel files, so the mount tilt correction reaches this export exactly as it reaches
/// GPX and FIT. Levelling happens in raw device axes, so it must run before the axis mapping below.
/// </summary>
public static class TelemetryOverlayCsv
{
    /// <summary>Header, units and formatting for one output column.</summary>
    private sealed record Column(string Header, string Units, string Format, double[] Values);

    private const double MsToKmh = 3.6;

    public static void Write(string path,
                             GpsSession? gps,
                             IReadOnlyList<ImuSample>? imu,
                             IReadOnlyList<ChannelSeries> channels,
                             IReadOnlyList<(int Number, double StartS, double DurationS)> laps,
                             TelemetryOverlayOptions opt,
                             Action<string>? log = null)
    {
        double rate = opt.RateHz > 0 ? opt.RateHz : 50;
        double step = 1.0 / rate;

        // One time base for everything: seconds from session start, the same base the channel files,
        // the IMU stream and the lap table all use.
        double end = 0;
        if (imu is { Count: > 0 }) end = Math.Max(end, imu[^1].ElapsedS);
        if (gps is { Samples.Count: > 0 }) end = Math.Max(end, gps.Samples[^1].TimeRel);
        foreach (var c in channels) if (c.Times.Count > 0) end = Math.Max(end, c.Times[^1]);
        if (end <= 0) throw new InvalidOperationException("nothing to write: no IMU, GPS or channel samples");

        int rows = (int)(end / step) + 1;
        var grid = new double[rows];
        for (int i = 0; i < rows; i++) grid[i] = Math.Round(i * step, 6);

        var cols = new List<Column> { new("Time", "s", "0.000", grid) };
        var used = new HashSet<string>(StringComparer.Ordinal) { "Time" };

        if (gps is { Samples.Count: > 0 })
        {
            // Each stream skips the fixes that lack it, and one the source never recorded gets no column:
            // heading and satellites come from a .vbo, not from a MyChron. Rows without a position are
            // left out as the GPX and FIT leave them out, or a gauge would dip to 0,0 between real fixes.
            void Add(string header, string units, string format, Func<GpsSample, double> value, bool angle = false)
            {
                var have = gps.Samples.Where(s => TrackBuilder.HasFix(s) && !double.IsNaN(value(s))).ToList();
                if (have.Count == 0) return;
                var t = have.Select(s => s.TimeRel).ToList();
                var v = have.Select(value).ToList();
                cols.Add(new(header, units, format, angle ? ResampleAngle(t, v, grid) : Resample(t, v, grid)));
                used.Add(header);
            }
            Add("GPS Speed", "km/h", "0.0000", s => s.SpeedMs * MsToKmh);
            Add("GPS Latitude", "deg", "0.00000000", s => s.Lat);
            Add("GPS Longitude", "deg", "0.00000000", s => s.Lon);
            Add("GPS Altitude", "m", "0.0000", s => s.Alt);
            Add("GPS Heading", "deg", "0.00", s => s.HeadingDeg, angle: true);
            Add("satellites", "sats", "0", s => s.Satellites);
        }

        if (imu is { Count: > 0 })
        {
            // Raw MyChron 6 IMU frame is X backward, Y right, Z up, so map it onto AiM's semantic
            // channels. This runs on already levelled samples; doing it the other way round would
            // rotate about the wrong axes.
            var t = imu.Select(s => s.ElapsedS).ToList();
            double lateralSign = opt.InvertLateral ? -1 : 1;
            cols.Add(new("InlineAcc", "g", "0.00", Resample(t, imu.Select(s => -s.Ax).ToList(), grid)));
            cols.Add(new("LateralAcc", "g", "0.00", Resample(t, imu.Select(s => lateralSign * s.Ay).ToList(), grid)));
            cols.Add(new("VerticalAcc", "g", "0.00", Resample(t, imu.Select(s => s.Az).ToList(), grid)));
            cols.Add(new("RollRate", "deg/s", "0.0", Resample(t, imu.Select(s => -s.Gx).ToList(), grid)));
            cols.Add(new("PitchRate", "deg/s", "0.0", Resample(t, imu.Select(s => s.Gy).ToList(), grid)));
            cols.Add(new("YawRate", "deg/s", "0.0", Resample(t, imu.Select(s => -s.Gz).ToList(), grid)));
            used.UnionWith(new[] { "InlineAcc", "LateralAcc", "VerticalAcc", "RollRate", "PitchRate", "YawRate" });
        }

        var fromImu = channels.Where(c => RaceStudioChannelScan.IsImuChannel(c.Name)).Select(c => c.Name).ToList();
        if (fromImu.Count > 0)
            log?.Invoke($"    {string.Join(", ", fromImu)}: taken from the levelled IMU stream, not re-read");

        // Sorted by name so an xrk and a CSV export of the same session produce the same column order,
        // whatever order the source happened to hand them over in.
        foreach (var ch in channels.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (RaceStudioChannelScan.IsImuChannel(ch.Name)) continue;   // comes from the levelled stream

            if (ch.AllZero)
            {
                log?.Invoke($"    {ch.Name}: skipped, all {ch.Values.Count} samples are zero (sensor not connected?)");
                continue;
            }

            var header = TelemetryOverlayAliases.Resolve(ch.Name, ch.Units);
            if (!used.Add(header.Header))
            {
                // Two channels wanting the same column: keep the first, pass the rest through raw.
                log?.Invoke($"    {ch.Name}: '{header.Header}' already taken, emitted verbatim as an extra stream");
                header = new ChannelHeader(ch.Name, ch.Units, ChannelBinding.Extra);
                if (!used.Add(header.Header)) continue;
            }
            else
            {
                log?.Invoke(header.Binding switch
                {
                    ChannelBinding.Alias => $"    {ch.Name} ({ch.Units}): bound as {header.Key}",
                    ChannelBinding.Renamed => $"    {ch.Name} ({ch.Units}): bound as {header.Key}, renamed to match",
                    _ => $"    {ch.Name} ({ch.Units}): no alias, emitted verbatim as an extra stream",
                });
            }
            if (ch.Constant) log?.Invoke($"      note: constant at {ch.Values[0].ToString("0.###", CultureInfo.InvariantCulture)}, kept anyway");

            var values = Resample(ch.Times, ch.Values, grid);
            // The steering gauge renders mirrored, so this negation is what makes it read correctly.
            if (opt.InvertSteering && header.Header.Equals("Steering Angle", StringComparison.Ordinal))
                for (int i = 0; i < values.Length; i++) values[i] = -values[i];

            cols.Add(new(header.Header, header.Units, "0.###", values));
        }

        if (laps.Count > 0) cols.Add(new("Lap", "#", "0", LapColumn(laps, grid)));

        WriteFile(path, cols, grid, laps, rate, opt);
        log?.Invoke($"    {rows} rows at {rate:0.#} Hz, {cols.Count} columns, {laps.Count} beacon marker(s)");
    }

    /// <summary>RaceStudio lap numbering, where the out-lap is 0. There is no "Lap" alias, so it lands
    /// as an extra stream.</summary>
    private static double[] LapColumn(IReadOnlyList<(int Number, double StartS, double DurationS)> laps, double[] grid)
    {
        var lap = new double[grid.Length];
        double step = grid.Length > 1 ? grid[1] - grid[0] : 0.02;
        foreach (var (number, startS, durationS) in laps)
        {
            int from = Math.Max(0, (int)(startS / step));
            int to = Math.Min(grid.Length - 1, (int)((startS + durationS) / step));
            for (int i = from; i <= to; i++) lap[i] = number - 1;
        }
        return lap;
    }

    /// <summary>Linear interpolation onto the grid, clamped at both ends rather than extrapolated.</summary>
    private static double[] Resample(IReadOnlyList<double> t, IReadOnlyList<double> v, double[] grid)
    {
        var outp = new double[grid.Length];
        if (t.Count == 0) return outp;
        if (t.Count == 1) { Array.Fill(outp, v[0]); return outp; }

        int j = 0;
        for (int i = 0; i < grid.Length; i++)
        {
            double x = grid[i];
            if (x <= t[0]) { outp[i] = v[0]; continue; }
            if (x >= t[^1]) { outp[i] = v[^1]; continue; }
            while (j + 1 < t.Count && t[j + 1] < x) j++;
            while (j > 0 && t[j] > x) j--;
            double t0 = t[j], t1 = t[j + 1];
            double f = t1 > t0 ? (x - t0) / (t1 - t0) : 0.0;
            outp[i] = v[j] + f * (v[j + 1] - v[j]);
        }
        return outp;
    }

    /// <summary>
    /// <see cref="Resample"/> for a compass angle: unwrapped first, so 359° to 1° passes through 0°
    /// rather than through 180°, and wrapped back into 0..360 afterwards.
    /// </summary>
    private static double[] ResampleAngle(IReadOnlyList<double> t, IReadOnlyList<double> deg, double[] grid)
    {
        var unwrapped = new double[deg.Count];
        for (int i = 0; i < deg.Count; i++)
        {
            if (i == 0) { unwrapped[0] = deg[0]; continue; }
            double d = deg[i] - deg[i - 1];
            unwrapped[i] = unwrapped[i - 1] + d - 360 * Math.Round(d / 360);   // the short way round
        }
        var outp = Resample(t, unwrapped, grid);
        for (int i = 0; i < outp.Length; i++) outp[i] = (outp[i] % 360 + 360) % 360;
        return outp;
    }

    private static void WriteFile(string path, List<Column> cols, double[] grid,
                                  IReadOnlyList<(int Number, double StartS, double DurationS)> laps,
                                  double rate, TelemetryOverlayOptions opt)
    {
        var ci = CultureInfo.InvariantCulture;
        // UTF-8 without a BOM, CRLF everywhere, every field quoted including the numbers.
        using var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20) { NewLine = "\r\n" };

        string date = "", time = "";
        if (opt.StartLocal is DateTime st)
        {
            // Invariant culture on purpose: the month and day names must be English whatever the
            // machine's locale is.
            date = st.ToString("dddd, MMMM ", ci) + st.Day.ToString(ci) + st.ToString(", yyyy", ci);
            time = st.ToString("h:mm tt", ci);
        }

        w.WriteLine(Row("Format", "AiM CSV File"));
        w.WriteLine(Row("Session", opt.SessionName));
        w.WriteLine(Row("Vehicle", opt.Vehicle));
        w.WriteLine(Row("Racer", opt.Racer));
        w.WriteLine(Row("Championship", opt.Championship));
        w.WriteLine(Row("Comment", ""));
        w.WriteLine(Row("Date", date));
        w.WriteLine(Row("Time", time));
        w.WriteLine(Row("Sample Rate", rate.ToString("0.###", ci)));
        w.WriteLine(Row("Duration", ((int)grid[^1]).ToString(ci)));
        w.WriteLine(Row("Segment", "Session"));
        // Beacon Markers is what drives the lap timer: cumulative lap-end times in seconds.
        w.WriteLine(Row(new[] { "Beacon Markers" }
            .Concat(laps.Select(l => (l.StartS + l.DurationS).ToString("0.###", ci)))));
        // Segment Times is read by nothing, but a genuine AiM export carries it.
        w.WriteLine(Row(new[] { "Segment Times" }.Concat(laps.Select(l => Segment(l.DurationS)))));
        w.WriteLine();
        w.WriteLine(Row(cols.Select(c => c.Header)));
        w.WriteLine(Row(cols.Select(c => c.Units)));
        w.WriteLine();

        var sb = new StringBuilder(256);
        for (int i = 0; i < grid.Length; i++)
        {
            sb.Clear();
            for (int c = 0; c < cols.Count; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append('"').Append(cols[c].Values[i].ToString(cols[c].Format, ci)).Append('"');
            }
            w.WriteLine(sb);
        }
    }

    private static string Segment(double seconds)
    {
        int m = (int)(seconds / 60);
        return string.Create(CultureInfo.InvariantCulture, $"{m}:{seconds - 60 * m:00.000}");
    }

    private static string Row(params string[] cells) => Row((IEnumerable<string>)cells);

    private static string Row(IEnumerable<string> cells) =>
        string.Join(",", cells.Select(c => '"' + (c ?? "").Replace("\"", "") + '"'));
}
