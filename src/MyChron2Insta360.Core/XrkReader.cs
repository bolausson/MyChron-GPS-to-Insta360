using System.Runtime.InteropServices;

namespace MyChron2Insta360.Core;

/// <summary>Session details an xrk file carries but a CSV export does not.</summary>
public sealed class XrkInfo
{
    public uint LoggerId;
    public string Vehicle = "", Track = "", Racer = "", Championship = "", SessionType = "";
    public DateTime? StartLocal;      // as recorded by the logger, in its own local time
    public double DurationS;
    public int GpsWeek;               // exact GPS week, so iTOW needs no calendar hint
    /// <summary>Bytes the parser stepped over, and the file size. A health signal, not an error:
    /// the format carries vendor blocks this tool has no use for.</summary>
    public long UnreadBytes, TotalBytes;

    public string Describe()
    {
        var parts = new List<string>();
        if (Track.Length > 0) parts.Add(Track);
        if (Racer.Length > 0) parts.Add(Racer);
        if (SessionType.Length > 0) parts.Add(SessionType);
        if (Championship.Length > 0) parts.Add(Championship);
        if (Vehicle.Length > 0) parts.Add("vehicle " + Vehicle);
        return string.Join(", ", parts);
    }
}

/// <summary>Everything one xrk/xrz file yields, in the same shapes the CSV reader produces.</summary>
public sealed class XrkData
{
    public GpsSession Gps { get; } = new();
    public List<ImuSample> Imu { get; } = new();
    public List<ChannelSeries> Channels { get; } = new();
    public List<(int Number, double StartS, double DurationS)> Laps { get; } = new();
    public XrkInfo Info { get; } = new();
}

/// <summary>
/// Reads a MyChron .xrk / .xrz directly through AiM's library, removing the "export to CSV first" step.
///
/// Verified against a RaceStudio CSV export of the same session: identical channel count and sample
/// times, values equal to the CSV's rounding (the CSV rounds accelerations to 2 decimals and most other
/// channels to 1, so this path is strictly more precise), 85678 GPS fixes with identical iTOW, and
/// positions agreeing to under a millimetre. It also supplies the GPS week number and the session's
/// track, racer and championship, none of which the CSV export contains.
/// </summary>
public static class XrkReader
{
    public static readonly string[] Extensions = { ".xrk", ".xrz" };

    public static bool IsXrkFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Point the loader at the folder holding AiM's DLLs. Must be set before the first use.</summary>
    public static void SetLibraryFolder(string? folder) => XrkNative.SearchPath = folder;

    /// <summary>Folders that were searched, for a helpful "not found" message.</summary>
    public static IEnumerable<string> SearchFolders() => XrkNative.SearchFolders();

    /// <summary>Always true: reading is done by our own parser, with no external library.</summary>
    public static bool Available => true;

    /// <summary>Whether AiM's library is also present, which enables the cross-check.</summary>
    public static bool DllAvailable => XrkNative.Available;
    public static string? DllUnavailableReason => XrkNative.Error;
    public static string? LibraryPath => XrkNative.LoadedFrom;
    public static string? UnavailableReason => null;

    /// <summary>Read a file with our own parser. This is the normal path.</summary>
    public static XrkData Load(string path) => XrkBinary.Load(path);

    /// <summary>Names of the six channels that become the IMU stream instead of ordinary channels.</summary>
    private static readonly string[] AccelNames = { "AccelerometerX", "AccelerometerY", "AccelerometerZ" };
    private static readonly string[] GyroNames = { "GyroX", "GyroY", "GyroZ" };

    /// <summary>Read the same file through AiM's library, for cross-checking our parser against it.</summary>
    public static XrkData LoadViaDll(string path)
    {
        if (!XrkNative.Available)
            throw new InvalidOperationException(XrkNative.Error ?? "the AiM XRK library is not available");

        string full = Path.GetFullPath(path);   // the API rejects relative paths
        int f = XrkNative.open_file(XrkNative.PathBytes(full));
        if (f <= 0)
        {
            string why = XrkNative.Str(XrkNative.get_last_open_error());
            throw new InvalidDataException($"could not open {Path.GetFileName(full)}" + (why.Length > 0 ? $": {why}" : ""));
        }

        try
        {
            var data = new XrkData();
            ReadInfo(f, data.Info);
            ReadLaps(f, data.Laps);
            ReadChannels(f, data);
            ReadGps(f, data, full);
            data.Gps.Name = Path.GetFileNameWithoutExtension(full);
            data.Gps.SourceFile = full;
            return data;
        }
        finally { XrkNative.close_file_i(f); }
    }

    private static void ReadInfo(int f, XrkInfo info)
    {
        info.LoggerId = XrkNative.get_logger_id(f);
        info.Vehicle = XrkNative.Str(XrkNative.get_vehicle_name(f));
        info.Track = XrkNative.Str(XrkNative.get_track_name(f));
        info.Racer = XrkNative.Str(XrkNative.get_racer_name(f));
        info.Championship = XrkNative.Str(XrkNative.get_championship_name(f));
        info.SessionType = XrkNative.Str(XrkNative.get_session_type_name(f));
        if (XrkNative.get_session_duration(f, out double dur) > 0) info.DurationS = dur;

        IntPtr tp = XrkNative.get_date_and_time(f);
        if (tp == IntPtr.Zero) return;
        var tm = Marshal.PtrToStructure<XrkNative.Tm>(tp);
        try
        {
            info.StartLocal = new DateTime(tm.Year + 1900, tm.Mon + 1, tm.MDay, tm.Hour, tm.Min, tm.Sec,
                                           DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException) { /* an unset clock, leave it null */ }
    }

    private static void ReadLaps(int f, List<(int, double, double)> laps)
    {
        int count = XrkNative.get_laps_count(f);
        for (int i = 0; i < count; i++)
            if (XrkNative.get_lap_info(f, i, out double start, out double dur) > 0 && dur > 0)
                laps.Add((i + 1, start, dur));
    }

    private static void ReadChannels(int f, XrkData data)
    {
        var accel = new Dictionary<string, (double[] T, double[] V)>(StringComparer.OrdinalIgnoreCase);
        var gyro = new Dictionary<string, (double[] T, double[] V)>(StringComparer.OrdinalIgnoreCase);

        int count = XrkNative.get_channels_count(f);
        for (int c = 0; c < count; c++)
        {
            string name = XrkNative.Str(XrkNative.get_channel_name(f, c));
            string units = XrkNative.Str(XrkNative.get_channel_units(f, c));
            int n = XrkNative.get_channel_samples_count(f, c);
            if (n <= 0) continue;

            var t = new double[n];
            var v = new double[n];
            if (XrkNative.get_channel_samples(f, c, t, v, n) <= 0) continue;

            if (AccelNames.Contains(name, StringComparer.OrdinalIgnoreCase)) { accel[name] = (t, v); continue; }
            if (GyroNames.Contains(name, StringComparer.OrdinalIgnoreCase)) { gyro[name] = (t, v); continue; }

            var series = new ChannelSeries { Name = name, Units = units, SourceFile = data.Gps.SourceFile };
            series.Times.AddRange(t);
            series.Values.AddRange(v);
            data.Channels.Add(series);
        }

        data.Imu.AddRange(ImuBuilder.Join(Triax(accel, AccelNames), Triax(gyro, GyroNames)));
    }

    private static List<ImuBuilder.Triax> Triax(Dictionary<string, (double[] T, double[] V)> src, string[] names)
    {
        var list = new List<ImuBuilder.Triax>();
        if (names.Any(n => !src.ContainsKey(n))) return list;
        var (t, x) = src[names[0]];
        var y = src[names[1]].V;
        var z = src[names[2]].V;
        int n = Math.Min(t.Length, Math.Min(x.Length, Math.Min(y.Length, z.Length)));
        for (int i = 0; i < n; i++) list.Add(new ImuBuilder.Triax(t[i], x[i], y[i], z[i]));
        return list;
    }

    /// <summary>
    /// Build the GPS track from the raw receiver solution, which is what the _GPS_o.csv export is made
    /// from: same rows, same timestamps, same iTOW. Position comes from the ECEF vector and speed from
    /// the ECEF velocity, both of which reproduce the CSV columns exactly.
    /// </summary>
    private static void ReadGps(int f, XrkData data, string path)
    {
        var raw = new Dictionary<string, (double[] T, double[] V)>(StringComparer.OrdinalIgnoreCase);
        int count = XrkNative.get_GPS_raw_channels_count(f);
        for (int c = 0; c < count; c++)
        {
            string name = XrkNative.Str(XrkNative.get_GPS_raw_channel_name(f, c));
            int n = XrkNative.get_GPS_raw_channel_samples_count(f, c);
            if (n <= 0) continue;
            var t = new double[n];
            var v = new double[n];
            if (XrkNative.get_GPS_raw_channel_samples(f, c, t, v, n) > 0) raw[name] = (t, v);
        }
        if (!raw.TryGetValue("ECEF position_X", out var px)) return;

        double[] X = px.V, T = px.T;
        double[] Y = Get(raw, "ECEF position_Y", X.Length);
        double[] Z = Get(raw, "ECEF position_Z", X.Length);
        double[] VX = Get(raw, "ECEF velocity_X", X.Length);
        double[] VY = Get(raw, "ECEF velocity_Y", X.Length);
        double[] VZ = Get(raw, "ECEF velocity_Z", X.Length);
        double[] IT = Get(raw, "ITOW", X.Length);
        double[] WK = Get(raw, "Week N", X.Length);

        if (WK.Length > 0 && WK[0] > 0)
        {
            data.Info.GpsWeek = (int)Math.Round(WK[0]);
            data.Gps.GpsWeek = data.Info.GpsWeek;
        }

        var accuracy = GpsAccuracy(f);
        int j = 0;
        for (int i = 0; i < X.Length; i++)
        {
            var (lat, lon, alt) = EcefToGeodetic(X[i], Y[i], Z[i]);
            double acc = 0;
            if (accuracy is { Length: > 0 } acc2)
            {
                double want = T[i];
                while (j + 1 < acc2.Length && Math.Abs(acc2[j + 1].T - want) <= Math.Abs(acc2[j].T - want)) j++;
                acc = acc2[j].V;
            }
            data.Gps.Samples.Add(new GpsSample
            {
                TimeRel = T[i],
                ItowMs = IT.Length > i ? IT[i] : double.NaN,
                Lat = lat,
                Lon = lon,
                Alt = alt,
                SpeedMs = Math.Sqrt(VX[i] * VX[i] + VY[i] * VY[i] + VZ[i] * VZ[i]),
                AccuracyM = acc,
            });
        }

        if (data.Gps.Samples.Count > 1)
        {
            double span = data.Gps.Samples[^1].TimeRel - data.Gps.Samples[0].TimeRel;
            if (span > 0) data.Gps.NativeHz = (data.Gps.Samples.Count - 1) / span;
        }
        // The logger's own clock is only a fallback: iTOW plus the week number fixes UTC exactly.
        data.Gps.DateHint = data.Info.StartLocal?.Date;
    }

    /// <summary>Position accuracy lives in the computed GPS channels, whose times are in milliseconds.</summary>
    private static (double T, double V)[]? GpsAccuracy(int f)
    {
        XrkNative.EnsureGpsFreq(25);
        int count = XrkNative.get_GPS_channels_count(f);
        for (int c = 0; c < count; c++)
        {
            if (!XrkNative.Str(XrkNative.get_GPS_channel_name(f, c)).Equals("GPS PosAccuracy", StringComparison.OrdinalIgnoreCase))
                continue;
            int n = XrkNative.get_GPS_channel_samples_count(f, c);
            if (n <= 0) return null;
            var t = new double[n];
            var v = new double[n];
            if (XrkNative.get_GPS_channel_samples(f, c, t, v, n) <= 0) return null;

            var outp = new (double, double)[n];
            for (int i = 0; i < n; i++) outp[i] = (t[i] / 1000.0, v[i]);   // ms here, seconds everywhere else
            return outp;
        }
        return null;
    }

    private static double[] Get(Dictionary<string, (double[] T, double[] V)> src, string name, int expected) =>
        src.TryGetValue(name, out var s) && s.V.Length >= expected ? s.V : new double[expected];

    /// <summary>WGS84 ECEF to latitude, longitude and ellipsoidal height (Bowring).</summary>
    internal static (double Lat, double Lon, double Alt) EcefToGeodetic(double x, double y, double z)
    {
        const double a = 6378137.0, f = 1.0 / 298.257223563;
        double b = a * (1 - f), e2 = f * (2 - f), ep2 = (a * a - b * b) / (b * b);
        double p = Math.Sqrt(x * x + y * y);
        if (p < 1e-9) return (z >= 0 ? 90 : -90, 0, Math.Abs(z) - b);

        double th = Math.Atan2(z * a, p * b);
        double lat = Math.Atan2(z + ep2 * b * Math.Pow(Math.Sin(th), 3),
                                p - e2 * a * Math.Pow(Math.Cos(th), 3));
        double n = a / Math.Sqrt(1 - e2 * Math.Sin(lat) * Math.Sin(lat));
        return (lat * 180.0 / Math.PI, Math.Atan2(y, x) * 180.0 / Math.PI, p / Math.Cos(lat) - n);
    }
}
