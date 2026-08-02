using System.Globalization;
using System.Text;

namespace MyChron2Insta360.Core;

/// <summary>
/// Writes inertial samples to a CSV with one fixed schema, identical for both the Insta360 and MyChron
/// sources: accelerometer in g, gyroscope in deg/s, an absolute UTC column (when known) and an
/// elapsed-seconds column. Axes are each device's own; the two sources are not cross-calibrated.
/// </summary>
public static class ImuCsvWriter
{
    /// <summary>
    /// Reduce an IMU stream to at most <paramref name="targetHz"/> samples per second. The Insta360 IMU
    /// runs at ~1 kHz, which makes very large files; 0 or a rate at/above the native one keeps everything.
    /// </summary>
    public static List<ImuSample> Downsample(IReadOnlyList<ImuSample> imu, double targetHz)
    {
        if (targetHz <= 0 || imu.Count == 0) return imu.ToList();
        double minInterval = 1.0 / targetHz;
        var outp = new List<ImuSample>(Math.Min(imu.Count, (int)(targetHz * 3600)));
        double last = double.NegativeInfinity;
        foreach (var s in imu)
        {
            if (s.ElapsedS - last < minInterval - 1e-9) continue;
            last = s.ElapsedS;
            outp.Add(s);
        }
        return outp;
    }

    public static void Write(string path, IReadOnlyList<ImuSample> imu)
    {
        var ci = CultureInfo.InvariantCulture;
        using var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20);
        w.WriteLine("utc,elapsed_s,accel_x_g,accel_y_g,accel_z_g,gyro_x_dps,gyro_y_dps,gyro_z_dps");

        var sb = new StringBuilder(96);
        foreach (var s in imu)
        {
            sb.Clear();
            if (s.Utc is DateTime u)
                sb.Append(DateTime.SpecifyKind(u, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", ci));
            sb.Append(',').Append(s.ElapsedS.ToString("0.###", ci))
              .Append(',').Append(s.Ax.ToString("0.####", ci))
              .Append(',').Append(s.Ay.ToString("0.####", ci))
              .Append(',').Append(s.Az.ToString("0.####", ci))
              .Append(',').Append(s.Gx.ToString("0.###", ci))
              .Append(',').Append(s.Gy.ToString("0.###", ci))
              .Append(',').Append(s.Gz.ToString("0.###", ci));
            w.WriteLine(sb);
        }
    }
}
