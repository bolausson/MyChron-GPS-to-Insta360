using System.Globalization;

namespace MyChron2Insta360.Core;

/// <summary>User-tunable options for a CSV -> track conversion.</summary>
public sealed class ConversionOptions
{
    /// <summary>Timezone of the session's local start time. Null = use the machine's local zone.</summary>
    public TimeZoneInfo? SessionTimeZone { get; set; }

    /// <summary>Fixed UTC offset for the local start time. If set, takes precedence over <see cref="SessionTimeZone"/>.</summary>
    public TimeSpan? FixedUtcOffset { get; set; }

    /// <summary>When no zone/offset is pinned, auto-detect the session timezone from the first GPS fix.</summary>
    public bool AutoTimeZone { get; set; } = true;

    /// <summary>Seconds added to every timestamp — fine-tunes sync against the camera clock.</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>Target output rate; points closer than 1/Hz apart are dropped. 0 = auto (match the detected GPS rate).</summary>
    public double TargetHz { get; set; }

    /// <summary>Output: the true GPS fix rate auto-detected from the log (null if none could be found).</summary>
    public double? DetectedGpsHz { get; set; }

    /// <summary>Output: the rate actually used for downsampling (either <see cref="TargetHz"/> or the detected rate).</summary>
    public double EffectiveHz { get; set; }

    /// <summary>Drop points with fewer than this many GPS satellites. 0 = no filtering.</summary>
    public int MinSatellites { get; set; }

    /// <summary>Trim leading/trailing near-stationary points so the map doesn't start/end on a blob. Off by default.</summary>
    public bool TrimStandstill { get; set; }

    /// <summary>Speed (km/h) below which a point counts as "standstill" for trimming.</summary>
    public double StandstillSpeedKmh { get; set; } = 2.0;
}

/// <summary>One output track point with an absolute UTC timestamp.</summary>
public sealed class TrackPoint
{
    public DateTime Utc { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double? EleM { get; set; }
    public double? SpeedMs { get; set; }
    public double? HeadingDeg { get; set; }
}

/// <summary>
/// Builds the absolute-UTC session start from the AiM header. The header Date+Time is LOCAL track
/// wall-clock with no timezone and only minute resolution, so a zone (or offset) must be supplied.
/// </summary>
public static class TimestampResolver
{
    private static readonly string[] DateFormats =
    {
        "dddd, MMMM d, yyyy", "dddd, MMMM dd, yyyy", "MMMM d, yyyy", "MMMM dd, yyyy",
        "M/d/yyyy", "d/M/yyyy", "yyyy-MM-dd"
    };

    private static readonly string[] TimeFormats =
    {
        "h:mm tt", "hh:mm tt", "h:mm:ss tt", "H:mm", "H:mm:ss", "HH:mm", "HH:mm:ss"
    };

    public static DateTime ResolveStartUtc(AimCsvData data, ConversionOptions opt)
    {
        var dateStr = data.GetMeta("Date");
        var timeStr = data.GetMeta("Time");
        if (string.IsNullOrWhiteSpace(dateStr))
            throw new FormatException("CSV header is missing the 'Date' field; cannot build absolute timestamps.");

        DateTime localStart = DateTime.SpecifyKind(ParseHeaderDateTime(dateStr!, timeStr), DateTimeKind.Unspecified);

        if (opt.FixedUtcOffset is TimeSpan off)
            return new DateTimeOffset(localStart, off).UtcDateTime;

        var tz = opt.SessionTimeZone ?? TimeZoneInfo.Local;
        return TimeZoneInfo.ConvertTimeToUtc(localStart, tz);
    }

    public static DateTime ParseHeaderDateTime(string dateStr, string? timeStr)
    {
        if (!DateTime.TryParseExact(dateStr.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && !DateTime.TryParse(dateStr.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            throw new FormatException($"Could not parse the Date header: '{dateStr}'.");

        if (!string.IsNullOrWhiteSpace(timeStr))
        {
            if (DateTime.TryParseExact(timeStr!.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                || DateTime.TryParse(timeStr!.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                date = date.Date + t.TimeOfDay;
        }
        return date;
    }
}

/// <summary>Turns parsed CSV rows into a clean, downsampled list of UTC-stamped track points.</summary>
public static class TrackBuilder
{
    public static List<TrackPoint> Build(AimCsvData data, ConversionOptions opt)
    {
        int iTime = data.IndexOf("Time");
        int iLat = data.IndexOf("GPS Latitude");
        int iLon = data.IndexOf("GPS Longitude");
        int iEle = data.IndexOf("GPS Altitude");
        int iSpd = data.IndexOf("GPS Speed");
        int iHdg = data.IndexOf("GPS Heading");
        int iSat = data.IndexOf("GPS Nsat");

        if (iTime < 0 || iLat < 0 || iLon < 0)
            throw new FormatException("CSV is missing required channels: Time, GPS Latitude, GPS Longitude.");

        // Auto-detect the session timezone from the first valid GPS fix (unless the caller pinned a zone/offset).
        if (opt.AutoTimeZone && opt.SessionTimeZone is null && opt.FixedUtcOffset is null)
        {
            foreach (var row in data.Rows)
            {
                double la = row[iLat], lo = row[iLon];
                if (double.IsNaN(la) || double.IsNaN(lo) || (la == 0 && lo == 0) || Math.Abs(la) > 90 || Math.Abs(lo) > 180)
                    continue;
                opt.SessionTimeZone = TimeZoneResolver.FromCoordinates(la, lo);
                break;
            }
        }

        DateTime startUtc = TimestampResolver.ResolveStartUtc(data, opt).AddSeconds(opt.ManualOffsetSeconds);

        // Resolve the output rate. TargetHz <= 0 means "match the true GPS rate", recovered from the
        // interpolation structure (the log is up-sampled to the 100 Hz IMU rate from a slower GNSS fix rate).
        double loggingHz = data.SampleRateHz;
        if (loggingHz <= 0 && data.Rows.Count > 1)
        {
            double d0 = data.Rows[1][iTime] - data.Rows[0][iTime];
            if (d0 > 0) loggingHz = 1.0 / d0;
        }
        if (loggingHz <= 0) loggingHz = 100;

        opt.DetectedGpsHz = GpsRateDetector.Detect(data, iTime, iSpd, iLat, iLon, loggingHz);
        opt.EffectiveHz = opt.TargetHz > 0 ? opt.TargetHz : (opt.DetectedGpsHz ?? loggingHz);

        double minInterval = opt.EffectiveHz > 0 ? 1.0 / opt.EffectiveHz : 0;
        double lastKeptT = double.NegativeInfinity;

        var pts = new List<TrackPoint>(data.Rows.Count);
        foreach (var row in data.Rows)
        {
            double t = row[iTime];
            if (double.IsNaN(t)) continue;

            double lat = row[iLat], lon = row[iLon];
            if (double.IsNaN(lat) || double.IsNaN(lon)) continue;
            if (lat == 0 && lon == 0) continue;                       // no-fix placeholder
            if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180) continue;  // out-of-range guard

            if (iSat >= 0 && opt.MinSatellites > 0 && !double.IsNaN(row[iSat]) && row[iSat] < opt.MinSatellites)
                continue;

            if (t - lastKeptT < minInterval) continue;                // downsample
            lastKeptT = t;

            pts.Add(new TrackPoint
            {
                Utc = startUtc.AddSeconds(t),
                Lat = lat,
                Lon = lon,
                EleM = iEle >= 0 && !double.IsNaN(row[iEle]) ? row[iEle] : null,
                SpeedMs = iSpd >= 0 && !double.IsNaN(row[iSpd]) ? row[iSpd] / 3.6 : null,  // km/h -> m/s
                HeadingDeg = iHdg >= 0 && !double.IsNaN(row[iHdg]) ? Normalize360(row[iHdg]) : null,  // AiM is +/-180; GPX course is 0..360
            });
        }

        if (opt.TrimStandstill && pts.Count > 0)
            pts = TrimStandstill(pts, opt.StandstillSpeedKmh / 3.6);

        return pts;
    }

    /// <summary>Wrap a heading into [0, 360). AiM's GPS Heading is reported as -180..+180.</summary>
    private static double Normalize360(double deg)
    {
        double d = deg % 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    private static List<TrackPoint> TrimStandstill(List<TrackPoint> pts, double thresholdMs)
    {
        int start = 0, end = pts.Count - 1;
        while (start < pts.Count && (pts[start].SpeedMs ?? 0) < thresholdMs) start++;
        while (end >= 0 && (pts[end].SpeedMs ?? 0) < thresholdMs) end--;
        if (start > end) return pts; // never moved above threshold — keep everything rather than nothing
        return pts.GetRange(start, end - start + 1);
    }
}
