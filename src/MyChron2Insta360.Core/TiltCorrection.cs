using System.Globalization;

namespace MyChron2Insta360.Core;

/// <summary>
/// The mount tilt found in an IMU stream, expressed as the one rotation that puts measured gravity back
/// onto the device's own vertical axis, together with the numbers that say how much to trust it.
/// </summary>
public sealed class TiltEstimate
{
    public double AngleDeg;                 // size of the levelling rotation
    public double AxisX, AxisY, AxisZ;      // unit rotation axis, in device axes
    public char VerticalAxis;               // 'X', 'Y' or 'Z': the axis gravity was referred to
    public int VerticalSign;                // +1 or -1
    public char LeanAAxis, LeanBAxis;       // the other two axes ...
    public double LeanADeg, LeanBDeg;       // ... and how far gravity leans into each
    public int Samples;                     // samples that made the estimate
    public int Blocks;                      // time blocks they were drawn from
    public double SpreadDeg;                // p10..p90 spread of the per-block angles
    public double GravityG;                 // length of the accepted mean vector, ~1.000 when clean
    public bool FromStatic;                 // false = fell back to the plain session mean
    public bool Ambiguous;                  // tilt is large enough that "which axis is up" is a guess

    /// <summary>
    /// One line for the log: the angle, where it leans, and how solid it is. Numbers are formatted with
    /// a dot so they can be typed straight back into the tilt field or --tilt on any locale.
    /// </summary>
    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{AngleDeg:0.0}° from {(VerticalSign < 0 ? "-" : "+")}{VerticalAxis} ") +
        string.Create(CultureInfo.InvariantCulture,
            $"(lean {LeanADeg:+0.0;-0.0}° toward {LeanAAxis}, {LeanBDeg:+0.0;-0.0}° toward {LeanBAxis}), ") +
        (FromStatic
            ? string.Create(CultureInfo.InvariantCulture,
                $"from {Samples} steady samples in {Blocks} block(s), spread {SpreadDeg:0.0}°, |g|={GravityG:0.000}")
            : string.Create(CultureInfo.InvariantCulture,
                $"from the session mean of {Samples} samples, |g|={GravityG:0.000} (no steady samples found)"));
}

/// <summary>
/// Finds and removes the mount tilt of a logger that is not sitting level: on a kart the MyChron is
/// angled on the steering wheel, so a slice of gravity sits permanently in the longitudinal channel and
/// every acceleration is mixed between axes.
///
/// Gravity is the only absolute reference an IMU carries, so the tilt is read off the accelerometer at
/// moments when the vehicle is barely accelerating or turning. Those moments are pooled per time block
/// and the blocks are combined with a MEDIAN, not a mean: a log usually ends with the vehicle parked
/// somewhere with its own slope, and one such block would otherwise drag the answer several degrees off.
/// </summary>
public static class TiltCorrection
{
    private const double StaticAccelTolG = 0.03;   // |a| within 1 g +- this counts as unaccelerated
    private const double StaticGyroDps = 3.0;      // and barely rotating
    private const int MinPerBlock = 10;
    private const int MinSamples = 50;
    private const double AmbiguousDeg = 40.0;      // past this, the nearest-axis choice is a guess

    /// <summary>
    /// Estimate the mount tilt. Returns null when the stream has no usable accelerometer data, with the
    /// reason in <paramref name="why"/>.
    /// </summary>
    public static TiltEstimate? Detect(IReadOnlyList<ImuSample> imu, out string? why)
    {
        why = null;
        if (imu is null || imu.Count == 0) { why = "no IMU samples"; return null; }

        // Group the steady samples by time block, so one long steady stretch (typically the vehicle
        // parked at the end) cannot outvote the rest of the session.
        double t0 = imu[0].ElapsedS, t1 = imu[^1].ElapsedS;
        double blockS = Math.Clamp((t1 - t0) / 24.0, 10.0, 120.0);
        var sums = new Dictionary<int, (double X, double Y, double Z, int N)>();
        int steady = 0;

        foreach (var s in imu)
        {
            double a = Math.Sqrt(s.Ax * s.Ax + s.Ay * s.Ay + s.Az * s.Az);
            if (Math.Abs(a - 1.0) > StaticAccelTolG) continue;
            double w = Math.Sqrt(s.Gx * s.Gx + s.Gy * s.Gy + s.Gz * s.Gz);
            if (w > StaticGyroDps) continue;
            steady++;
            int b = (int)((s.ElapsedS - t0) / blockS);
            var c = sums.TryGetValue(b, out var v) ? v : default;
            sums[b] = (c.X + s.Ax, c.Y + s.Ay, c.Z + s.Az, c.N + 1);
        }

        var blocks = sums.Values.Where(v => v.N >= MinPerBlock)
                                .Select(v => Normalize(v.X / v.N, v.Y / v.N, v.Z / v.N))
                                .Where(v => v.Ok).ToList();

        double gx, gy, gz, magnitude;
        int used;
        bool fromStatic = true;
        double spread = 0;

        if (blocks.Count >= 3)
        {
            // Median of the per-block gravity directions, component by component.
            gx = Median(blocks.Select(b => b.X).ToList());
            gy = Median(blocks.Select(b => b.Y).ToList());
            gz = Median(blocks.Select(b => b.Z).ToList());
            var n = Normalize(gx, gy, gz);
            if (!n.Ok) { why = "gravity direction is degenerate"; return null; }
            (gx, gy, gz) = (n.X, n.Y, n.Z);
            magnitude = AverageMagnitude(imu, t0, blockS, sums);
            used = steady;

            // How far apart the blocks are is the honest confidence signal.
            var angles = blocks.Select(b => Angle(b.X, b.Y, b.Z, gx, gy, gz)).OrderBy(v => v).ToList();
            spread = Percentile(angles, 0.9) - Percentile(angles, 0.1);
        }
        else if (steady >= MinSamples)
        {
            double sx = 0, sy = 0, sz = 0;
            foreach (var v in sums.Values) { sx += v.X; sy += v.Y; sz += v.Z; }
            var n = Normalize(sx / steady, sy / steady, sz / steady);
            if (!n.Ok) { why = "gravity direction is degenerate"; return null; }
            (gx, gy, gz) = (n.X, n.Y, n.Z);
            magnitude = Math.Sqrt(sx * sx + sy * sy + sz * sz) / steady;
            used = steady;
        }
        else
        {
            // Nothing steady anywhere: fall back to the whole-session mean. Over a closed circuit the
            // vehicle's own accelerations largely cancel, so this still points roughly at gravity, but
            // vertical shocks do not cancel and it can be several degrees out.
            double sx = 0, sy = 0, sz = 0;
            foreach (var s in imu) { sx += s.Ax; sy += s.Ay; sz += s.Az; }
            var n = Normalize(sx / imu.Count, sy / imu.Count, sz / imu.Count);
            if (!n.Ok) { why = "no accelerometer data"; return null; }
            (gx, gy, gz) = (n.X, n.Y, n.Z);
            magnitude = Math.Sqrt(sx * sx + sy * sy + sz * sz) / imu.Count;
            used = imu.Count;
            fromStatic = false;
        }

        // Refer gravity to whichever device axis it is closest to, and level onto that one.
        double ax = Math.Abs(gx), ay = Math.Abs(gy), az = Math.Abs(gz);
        char vertical; int sign; double alongVertical;
        if (az >= ax && az >= ay) { vertical = 'Z'; sign = Math.Sign(gz); alongVertical = az; }
        else if (ay >= ax) { vertical = 'Y'; sign = Math.Sign(gy); alongVertical = ay; }
        else { vertical = 'X'; sign = Math.Sign(gx); alongVertical = ax; }
        if (sign == 0) sign = 1;

        double vx = vertical == 'X' ? sign : 0, vy = vertical == 'Y' ? sign : 0, vz = vertical == 'Z' ? sign : 0;
        double angle = Math.Acos(Math.Clamp(alongVertical, -1.0, 1.0)) * 180.0 / Math.PI;

        // Rotating about g x v by that angle carries gravity exactly onto the vertical axis.
        var axis = Normalize(gy * vz - gz * vy, gz * vx - gx * vz, gx * vy - gy * vx);
        var est = new TiltEstimate
        {
            AngleDeg = angle,
            AxisX = axis.Ok ? axis.X : 1, AxisY = axis.Ok ? axis.Y : 0, AxisZ = axis.Ok ? axis.Z : 0,
            VerticalAxis = vertical,
            VerticalSign = sign,
            Samples = used,
            Blocks = blocks.Count,
            SpreadDeg = spread,
            GravityG = magnitude,
            FromStatic = fromStatic,
            Ambiguous = angle >= AmbiguousDeg,
        };

        // Split the lean over the two non-vertical axes, which is what a person can picture. The
        // reference is gravity's projection ONTO the vertical axis, always positive, so the breakdown
        // stays sensible when that axis is a negative one (say gravity along -X).
        double along = alongVertical;
        (est.LeanAAxis, est.LeanADeg, est.LeanBAxis, est.LeanBDeg) = vertical switch
        {
            'Z' => ('X', Deg(Math.Atan2(gx, along)), 'Y', Deg(Math.Atan2(gy, along))),
            'Y' => ('X', Deg(Math.Atan2(gx, along)), 'Z', Deg(Math.Atan2(gz, along))),
            _ => ('Y', Deg(Math.Atan2(gy, along)), 'Z', Deg(Math.Atan2(gz, along))),
        };
        return est;
    }

    /// <summary>
    /// Rotate every accelerometer and gyroscope reading by the levelling rotation, in place. Passing
    /// <paramref name="overrideAngleDeg"/> keeps the detected tilt direction but uses a known angle.
    /// </summary>
    public static void Apply(IReadOnlyList<ImuSample> imu, TiltEstimate est, double? overrideAngleDeg = null)
    {
        if (imu is null || imu.Count == 0 || est is null) return;
        Apply(imu, overrideAngleDeg ?? est.AngleDeg, est.AxisX, est.AxisY, est.AxisZ);
    }

    /// <summary>Rotate accelerometer and gyroscope vectors about an arbitrary axis (Rodrigues).</summary>
    public static void Apply(IReadOnlyList<ImuSample> imu, double angleDeg, double axX, double axY, double axZ)
    {
        var n = Normalize(axX, axY, axZ);
        if (!n.Ok || Math.Abs(angleDeg) < 1e-9) return;

        double t = angleDeg * Math.PI / 180.0, c = Math.Cos(t), s = Math.Sin(t), k = 1 - c;
        double x = n.X, y = n.Y, z = n.Z;
        double m00 = c + x * x * k, m01 = x * y * k - z * s, m02 = x * z * k + y * s;
        double m10 = y * x * k + z * s, m11 = c + y * y * k, m12 = y * z * k - x * s;
        double m20 = z * x * k - y * s, m21 = z * y * k + x * s, m22 = c + z * z * k;

        foreach (var p in imu)
        {
            double a0 = p.Ax, a1 = p.Ay, a2 = p.Az;
            p.Ax = m00 * a0 + m01 * a1 + m02 * a2;
            p.Ay = m10 * a0 + m11 * a1 + m12 * a2;
            p.Az = m20 * a0 + m21 * a1 + m22 * a2;

            double g0 = p.Gx, g1 = p.Gy, g2 = p.Gz;
            p.Gx = m00 * g0 + m01 * g1 + m02 * g2;
            p.Gy = m10 * g0 + m11 * g1 + m12 * g2;
            p.Gz = m20 * g0 + m21 * g1 + m22 * g2;
        }
    }

    private static double Deg(double rad) => rad * 180.0 / Math.PI;

    private static (bool Ok, double X, double Y, double Z) Normalize(double x, double y, double z)
    {
        double m = Math.Sqrt(x * x + y * y + z * z);
        return m < 1e-9 ? (false, 0, 0, 0) : (true, x / m, y / m, z / m);
    }

    private static double Angle(double ax, double ay, double az, double bx, double by, double bz) =>
        Deg(Math.Acos(Math.Clamp(ax * bx + ay * by + az * bz, -1.0, 1.0)));

    private static double Median(List<double> v)
    {
        v.Sort();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : 0.5 * (v[n / 2 - 1] + v[n / 2]);
    }

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

    /// <summary>Mean length of the per-block gravity vectors: ~1.000 g when the samples really were steady.</summary>
    private static double AverageMagnitude(IReadOnlyList<ImuSample> imu, double t0, double blockS,
                                           Dictionary<int, (double X, double Y, double Z, int N)> sums)
    {
        double total = 0; int n = 0;
        foreach (var v in sums.Values)
        {
            if (v.N < MinPerBlock) continue;
            total += Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z) / v.N;
            n++;
        }
        return n == 0 ? 0 : total / n;
    }
}
