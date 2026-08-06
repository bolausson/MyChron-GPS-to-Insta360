using System.Globalization;
using MyChron2Insta360.Core;

namespace MyChron2Insta360.App;

/// <summary>
/// Applies the mount-tilt correction across a whole run. The angle is worked out once, from the first
/// stream that can supply it, and then reused: the logger does not move between sessions, and a
/// per-session angle would make merged output inconsistent with itself.
/// </summary>
internal sealed class TiltPlan
{
    public bool Enabled { get; init; }
    public double? ManualDeg { get; init; }   // null = measure it from the data

    private TiltEstimate? _est;
    private bool _reported;

    public TiltEstimate? Estimate => _est;

    /// <summary>Level one IMU stream in place. Safe to call for every session or clip.</summary>
    public void ApplyTo(IReadOnlyList<ImuSample> imu, Action<string> log)
    {
        if (!Enabled || imu is null || imu.Count == 0) return;

        if (_est is null)
        {
            _est = TiltCorrection.Detect(imu, out string? why);
            if (_est is null)
            {
                if (!_reported) { _reported = true; log($"  Tilt correction: cannot measure the tilt ({why}); IMU left as recorded."); }
                return;
            }
            if (!_reported)
            {
                _reported = true;
                foreach (var line in Describe(_est, ManualDeg)) log(line);
            }
        }
        TiltCorrection.Apply(imu, _est, ManualDeg);
    }

    /// <summary>The log lines for an estimate, including the warnings worth surfacing.</summary>
    public static IEnumerable<string> Describe(TiltEstimate est, double? manualDeg = null)
    {
        var ci = CultureInfo.InvariantCulture;
        yield return $"  Tilt detected: {est.Describe()}";
        yield return manualDeg is double m
            ? string.Create(ci, $"  Tilt applied:  {m:0.0}° (entered by hand), about the detected tilt direction.")
            : string.Create(ci, $"  Tilt applied:  {est.AngleDeg:0.0}°.");

        if (!est.FromStatic)
            yield return "  Note: no steady moments were found, so this came from the session average and may be a few degrees out.";
        else if (est.SpreadDeg > 3.0)
            yield return string.Create(ci, $"  Note: the estimate varies by {est.SpreadDeg:0.0}° across the session; the mount may have moved.");
        if (est.GravityG is > 0 and (< 0.95 or > 1.05))
            yield return string.Create(ci, $"  Note: the steady samples average {est.GravityG:0.000} g rather than 1.000 g, so treat the angle with care.");
        if (est.Ambiguous)
            yield return string.Create(ci, $"  Note: at {est.AngleDeg:0.0}° the choice of which axis is 'up' is a guess; check it before trusting the result.");
    }

    /// <summary>
    /// Measure the tilt from the first input that carries an IMU, without converting anything. Backs the
    /// GUI's Autodetect button and the CLI's --detect-tilt.
    /// </summary>
    public static TiltEstimate? DetectFrom(IEnumerable<string> inputs, out string source, out string? why)
    {
        source = "";
        why = null;
        var paths = inputs.ToList();

        var clips = Insta360Trailer.DiscoverClips(paths);
        if (clips.Count > 0)
        {
            foreach (var clip in clips)
            {
                var tel = Insta360Trailer.ParseClip(clip, out string used);
                if (tel.Imu.Count == 0) continue;
                source = Path.GetFileName(used);
                return TiltCorrection.Detect(tel.Imu, out why);
            }
            why = "the video has no IMU data";
            return null;
        }

        List<MyChronSource> sources;
        try { sources = MyChronSources.LoadAll(paths); }
        catch (Exception e) { why = e.Message; return null; }

        foreach (var s in sources)
        {
            var imu = s.LoadImu();
            if (imu.Count == 0) continue;
            source = s.BaseName;
            return TiltCorrection.Detect(imu, out why);
        }
        why = "no accelerometer data found in the selected input(s)";
        return null;
    }
}
