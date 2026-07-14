namespace MyChron2Insta360.Core;

/// <summary>User-tunable options for turning GPS session(s) into an output track.</summary>
public sealed class ConversionOptions
{
    /// <summary>Seconds added to every timestamp — fine-tunes sync against the camera clock.</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>Target output rate. 0 = keep the file's native rate (no downsampling).</summary>
    public double TargetHz { get; set; }

    /// <summary>Drop fixes whose reported accuracy is worse than this many metres. 0 = keep all.</summary>
    public double MaxAccuracyM { get; set; }

    /// <summary>Trim leading/trailing near-stationary points (per session) so the map doesn't start/end on a blob.</summary>
    public bool TrimStandstill { get; set; }

    /// <summary>Speed (m/s) below which a point counts as "standstill" for trimming (~2 km/h).</summary>
    public double StandstillSpeedMs { get; set; } = 0.6;

    /// <summary>Override the date used to resolve the GPS week (else taken from each folder path / file time).</summary>
    public DateTime? DateOverrideUtc { get; set; }

    /// <summary>GPS − UTC leap seconds applied to iTOW.</summary>
    public int LeapSeconds { get; set; } = ItowTime.DefaultLeapSeconds;

    // ---- outputs, filled in by TrackBuilder ----
    public double EffectiveHz { get; set; }
    public int SessionCount { get; set; }
    public int TotalPoints { get; set; }
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public bool UsedItow { get; set; }
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
/// Turns one or more GPS sessions into UTC-stamped track segments (one segment per session, in
/// chronological order). Because every point's time comes from GPS iTOW, sessions from the same day
/// share a single clock and line up on one continuous timeline with natural gaps between them.
/// </summary>
public static class TrackBuilder
{
    public static List<List<TrackPoint>> Build(IReadOnlyList<GpsSession> sessions, ConversionOptions opt)
    {
        var segments = new List<List<TrackPoint>>();
        opt.UsedItow = false;
        double? firstNativeHz = null;

        foreach (var session in sessions)
        {
            firstNativeHz ??= session.NativeHz;
            bool haveItow = session.Samples.Any(s => !double.IsNaN(s.ItowMs) && s.ItowMs > 0);
            DateTime? dateHint = opt.DateOverrideUtc ?? session.DateHint;
            if (haveItow && dateHint is null)
                throw new InvalidOperationException($"Could not determine the date for '{Path.GetFileName(session.SourceFile)}'.");
            if (haveItow) opt.UsedItow = true;

            double minInterval = opt.TargetHz > 0 ? 1.0 / opt.TargetHz : 0; // 0 => keep every native sample
            double lastKept = double.NegativeInfinity;

            var pts = new List<TrackPoint>();
            foreach (var s in session.Samples)
            {
                if (double.IsNaN(s.Lat) || double.IsNaN(s.Lon)) continue;
                if (s.Lat == 0 && s.Lon == 0) continue;
                if (Math.Abs(s.Lat) > 90 || Math.Abs(s.Lon) > 180) continue;
                if (opt.MaxAccuracyM > 0 && !double.IsNaN(s.AccuracyM) && s.AccuracyM > opt.MaxAccuracyM) continue;
                if (minInterval > 0 && s.TimeRel - lastKept < minInterval - 1e-6) continue;
                lastKept = s.TimeRel;

                DateTime utc = haveItow && !double.IsNaN(s.ItowMs) && s.ItowMs > 0
                    ? ItowTime.ToUtc(s.ItowMs, dateHint!.Value, opt.LeapSeconds)
                    : dateHint!.Value.Date.AddSeconds(s.TimeRel); // degraded fallback
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddSeconds(opt.ManualOffsetSeconds);

                pts.Add(new TrackPoint
                {
                    Utc = utc,
                    Lat = s.Lat,
                    Lon = s.Lon,
                    EleM = double.IsNaN(s.Alt) ? null : s.Alt,
                    SpeedMs = double.IsNaN(s.SpeedMs) ? null : s.SpeedMs,
                    HeadingDeg = null,
                });
            }

            if (opt.TrimStandstill && pts.Count > 0)
                pts = TrimStandstill(pts, opt.StandstillSpeedMs);
            if (pts.Count > 0)
                segments.Add(pts);
        }

        segments.Sort((a, b) => a[0].Utc.CompareTo(b[0].Utc)); // chronological across sessions

        opt.SessionCount = segments.Count;
        opt.TotalPoints = segments.Sum(s => s.Count);
        opt.EffectiveHz = opt.TargetHz > 0 ? opt.TargetHz : (firstNativeHz ?? 0);
        opt.StartUtc = segments.Count > 0 ? segments[0][0].Utc : null;
        opt.EndUtc = segments.Count > 0 ? segments[^1][^1].Utc : null;
        return segments;
    }

    private static List<TrackPoint> TrimStandstill(List<TrackPoint> pts, double thresholdMs)
    {
        int start = 0, end = pts.Count - 1;
        while (start < pts.Count && (pts[start].SpeedMs ?? 0) < thresholdMs) start++;
        while (end >= 0 && (pts[end].SpeedMs ?? 0) < thresholdMs) end--;
        if (start > end) return pts;
        return pts.GetRange(start, end - start + 1);
    }
}
