namespace MyChron2Insta360.Core;

/// <summary>User-tunable options for turning GPS session(s) into an output track.</summary>
public sealed class ConversionOptions
{
    /// <summary>Seconds added to every timestamp, fine-tunes sync against the camera clock. Insta360 video is never nudged.</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>Target output rate. 0 = keep the file's native rate (no downsampling).</summary>
    public double TargetHz { get; set; }

    /// <summary>Drop fixes whose reported accuracy is worse than this many metres. 0 = keep all.</summary>
    public double MaxAccuracyM { get; set; }

    /// <summary>Trim leading/trailing near-stationary points (per session) so the map doesn't start/end on a blob.</summary>
    public bool TrimStandstill { get; set; }

    /// <summary>Speed (m/s) below which a point counts as "standstill" for trimming (~2 km/h).</summary>
    public double StandstillSpeedMs { get; set; } = 0.6;

    /// <summary>
    /// Override the date used to resolve the GPS week (else taken from each folder path / file time).
    /// A .vbo, which records only the time of day, takes it as the UTC date of its first fix instead.
    /// </summary>
    public DateTime? DateOverrideUtc { get; set; }

    /// <summary>GPS - UTC leap seconds applied to iTOW.</summary>
    public int LeapSeconds { get; set; } = ItowTime.DefaultLeapSeconds;

    // ---- outputs, filled in by TrackBuilder ----
    public double EffectiveHz { get; set; }
    public int SessionCount { get; set; }
    public int TotalPoints { get; set; }
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public bool UsedItow { get; set; }
    /// <summary>A session took its clock from the UTC its file states (a .vbo).</summary>
    public bool UsedFileUtc { get; set; }
    /// <summary>The fixed rate really dropped fixes; asking for the native rate or more does not.</summary>
    public bool Downsampled { get; set; }
    /// <summary>Sessions in one build that cover the same time, which a merge cannot show as one track.</summary>
    public List<string> Overlaps { get; } = new();

    /// <summary>Where the timestamps came from, for the log.</summary>
    public string DescribeTimeBase() => (UsedItow, UsedFileUtc) switch
    {
        (true, true) => "iTOW (exact GPS time), and the UTC stated in the .vbo",
        (true, false) => "iTOW (exact GPS time)",
        (false, true) => "the UTC stated in the .vbo",
        _ => "relative (no GPS time), sync will need a nudge",
    };

    /// <summary>The time nudge as the log states it: signed, and exactly as applied, to the 100 ns tick.</summary>
    public string DescribeNudge()
    {
        long ticks = (long)Math.Round(ManualOffsetSeconds * TimeSpan.TicksPerSecond);   // as Clock.PlusSeconds adds it
        if (ticks == 0) return "none";
        return $"{Seconds(ticks)} s, every timestamp moved {Seconds(Math.Abs(ticks)).TrimStart('+')} s " +
               (ticks < 0 ? "earlier" : "later");
    }

    /// <summary>Signed seconds for the log, every digit a tick carries and no more.</summary>
    public static string Seconds(long ticks) =>
        (ticks / (double)TimeSpan.TicksPerSecond).ToString("+0.#######;-0.#######", System.Globalization.CultureInfo.CurrentCulture);
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
        var names = new Dictionary<List<TrackPoint>, string>(ReferenceEqualityComparer.Instance);
        double? firstNativeHz = null;

        foreach (var session in sessions)
        {
            firstNativeHz ??= session.NativeHz;
            // A .vbo states UTC outright, so it needs neither a GPS week nor leap seconds.
            DateTime? fileUtc = session.StartUtc;
            bool haveItow = fileUtc is null && session.Samples.Any(s => !double.IsNaN(s.ItowMs) && s.ItowMs > 0);
            // An xrk carries the GPS week itself, so no calendar hint is needed to place the iTOW.
            int? week = opt.DateOverrideUtc is null ? session.GpsWeek : null;
            DateTime? dateHint = opt.DateOverrideUtc ?? session.DateHint;
            // The first fix places the session, and every other fix goes in the GPS week nearest it, so
            // a session is never split, and runs on past the change of week, Saturday to Sunday at
            // midnight GPS time, where iTOW starts again from 0 while an xrk's week stays.
            DateTime? anchor = FirstFixUtc(session, opt, out _);
            var usable = session.Samples.Where(s => Usable(s, opt)).ToList();
            // A session without a single fix (the GPS never locked) has nothing to place, so it is not an error.
            if (usable.Count > 0 && fileUtc is null && week is null && dateHint is null)
                throw new InvalidOperationException($"Could not determine the date for '{Path.GetFileName(session.SourceFile)}'.");
            if (haveItow) opt.UsedItow = true;
            if (fileUtc is not null) opt.UsedFileUtc = true;

            var pts = new List<TrackPoint>(usable.Count);
            foreach (var s in Thinning.ToRate(usable, s => s.TimeRel, opt.TargetHz))
            {
                // iTOW 0 is the first instant of a GPS week, a real time: it fell back to the date before,
                // which failed an xrk with a fix on the stroke of the week and threw a CSV one's a day off.
                DateTime utc = fileUtc is DateTime f0 ? f0.PlusSeconds(s.TimeRel)
                    : haveItow && !double.IsNaN(s.ItowMs) && s.ItowMs >= 0
                        ? ItowTime.ToUtc(s.ItowMs, anchor!.Value, opt.LeapSeconds)
                        : dateHint!.Value.Date.PlusSeconds(s.TimeRel); // degraded fallback
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc).PlusSeconds(opt.ManualOffsetSeconds);

                pts.Add(new TrackPoint
                {
                    Utc = utc,
                    Lat = s.Lat,
                    Lon = s.Lon,
                    EleM = double.IsNaN(s.Alt) ? null : s.Alt,
                    SpeedMs = double.IsNaN(s.SpeedMs) ? null : s.SpeedMs,
                    HeadingDeg = double.IsNaN(s.HeadingDeg) ? null : s.HeadingDeg,
                });
            }

            if (opt.TrimStandstill && pts.Count > 0)
                pts = TrimStandstill(pts, opt.StandstillSpeedMs);
            if (pts.Count > 0)
            {
                segments.Add(pts);
                names[pts] = session.Name;
            }
        }

        segments.Sort((a, b) => a[0].Utc.CompareTo(b[0].Utc)); // chronological across sessions

        // Two recordings of the same stretch (a second logger on the same kart, say) cannot share one
        // timeline: the merged file would run over it twice, back in time at the join.
        for (int i = 1, last = 0; i < segments.Count; i++)
        {
            double overlap = (segments[last][^1].Utc - segments[i][0].Utc).TotalSeconds;
            if (overlap > 1)
                opt.Overlaps.Add($"'{names[segments[last]]}' and '{names[segments[i]]}' overlap by {overlap:0} s");
            if (segments[i][^1].Utc > segments[last][^1].Utc) last = i;
        }

        opt.SessionCount = segments.Count;
        opt.TotalPoints = segments.Sum(s => s.Count);
        // Asking for more than the file holds keeps every fix, so report what was really written.
        double native = firstNativeHz ?? 0;
        opt.Downsampled = opt.TargetHz > 0 && (native <= 0 || opt.TargetHz < native);
        opt.EffectiveHz = opt.Downsampled ? opt.TargetHz : native;
        opt.StartUtc = segments.Count > 0 ? segments[0][0].Utc : null;
        opt.EndUtc = segments.Count > 0 ? segments.Max(s => s[^1].Utc) : null;
        return segments;
    }

    /// <summary>
    /// UTC at a session's time zero, the instant its IMU, laps and temperature are measured from, with
    /// the nudge applied just as it is to every fix. Trimming and downsampling do not move it, so those
    /// streams stay on the track however the track itself was built.
    /// </summary>
    public static DateTime? SessionStartUtc(GpsSession session, ConversionOptions opt)
    {
        DateTime? start = session.StartUtc;
        if (start is null)
        {
            if (FirstFixUtc(session, opt, out var first) is DateTime f) start = f.PlusSeconds(-first!.TimeRel);
            else if (first is null && (opt.DateOverrideUtc ?? session.DateHint) is DateTime d)
                start = d.Date;   // the same degraded clock the track falls back to
        }
        return start is DateTime u ? DateTime.SpecifyKind(u, DateTimeKind.Utc).PlusSeconds(opt.ManualOffsetSeconds) : null;
    }

    /// <summary>
    /// UTC of a session's first fix with GPS time, which places the whole session. An xrk states its
    /// GPS week. Otherwise a date picks it: a folder date or --date is the day of the session, so the
    /// week nearest it; a file's write time comes after the session, days after at times, so its last
    /// fix goes in the last week that does not put it after that, and the first fix in the week nearest
    /// the last. Taking the week nearest a write time put an export written four days on a week late,
    /// and could split a session across two weeks.
    /// </summary>
    private static DateTime? FirstFixUtc(GpsSession session, ConversionOptions opt, out GpsSample? first)
    {
        first = session.Samples.FirstOrDefault(g => !double.IsNaN(g.ItowMs) && g.ItowMs > 0);
        if (first is null) return null;
        if (opt.DateOverrideUtc is DateTime forced) return ItowTime.ToUtc(first.ItowMs, forced, opt.LeapSeconds);
        if (session.GpsWeek is int w) return ItowTime.FromWeek(w, first.ItowMs, opt.LeapSeconds);
        if (session.DateHint is not DateTime hint) return null;
        if (!session.DateHintIsFileTime) return ItowTime.ToUtc(first.ItowMs, hint, opt.LeapSeconds);
        var last = session.Samples.Last(g => !double.IsNaN(g.ItowMs) && g.ItowMs > 0);
        return ItowTime.ToUtc(first.ItowMs, ItowTime.ToUtcNotAfter(last.ItowMs, hint, opt.LeapSeconds), opt.LeapSeconds);
    }

    /// <summary>A fix with a real position: not missing, not the 0,0 a receiver reports before it locks.</summary>
    public static bool HasFix(GpsSample s) =>
        !double.IsNaN(s.Lat) && !double.IsNaN(s.Lon)
        && !(s.Lat == 0 && s.Lon == 0)
        && Math.Abs(s.Lat) <= 90 && Math.Abs(s.Lon) <= 180;

    private static bool Usable(GpsSample s, ConversionOptions opt) =>
        HasFix(s) && !(opt.MaxAccuracyM > 0 && !double.IsNaN(s.AccuracyM) && s.AccuracyM > opt.MaxAccuracyM);

    private static List<TrackPoint> TrimStandstill(List<TrackPoint> pts, double thresholdMs)
    {
        int start = 0, end = pts.Count - 1;
        while (start < pts.Count && (pts[start].SpeedMs ?? 0) < thresholdMs) start++;
        while (end >= 0 && (pts[end].SpeedMs ?? 0) < thresholdMs) end--;
        if (start > end) return pts;
        return pts.GetRange(start, end - start + 1);
    }
}

/// <summary>
/// Thins a time series to a target rate by keeping the first sample in each slot of a regular grid.
/// Spacing each kept sample from the previous one instead drifts low whenever the native period does
/// not divide the target one: 25 Hz asked down to 10 Hz kept every third fix, 8.3 Hz.
/// </summary>
public static class Thinning
{
    /// <summary>The samples to keep, in order. 0 Hz, or a rate at or above the native one, keeps all.
    /// The items must be in time order.</summary>
    public static List<T> ToRate<T>(IReadOnlyList<T> items, Func<T, double> seconds, double hz)
    {
        if (hz <= 0 || items.Count == 0) return items.ToList();
        double step = 1.0 / hz, origin = seconds(items[0]), due = origin;
        var kept = new List<T>();
        foreach (var item in items)
        {
            double t = seconds(item);
            if (t < due - 1e-9) continue;
            kept.Add(item);
            // The slot after this sample, counted from the origin so rounding cannot build up, and
            // straight past any gap rather than catching up afterwards with a burst.
            due = origin + (Math.Floor((t - origin) / step + 1e-9) + 1) * step;
        }
        return kept;
    }
}
