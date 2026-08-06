using System.Globalization;

namespace MyChron2Insta360.Core;

/// <summary>
/// Reads the MyChron IMU from a RaceStudio "_CSV" folder: AccelerometerX/Y/Z.csv (g) and GyroX/Y/Z.csv
/// (deg/s), each a per-channel `time,value` file. Accel and gyro start at slightly different times and
/// drift against each other, so gyro is joined to each accel sample by nearest timestamp. Produces the
/// same <see cref="ImuSample"/>
/// (accel g, gyro deg/s) as the Insta360 source, so both write an identical IMU CSV.
/// </summary>
public static class RaceStudioImu
{
    public static List<ImuSample> Load(string folder)
    {
        var accel = ReadTriax(folder, "AccelerometerX.csv", "AccelerometerY.csv", "AccelerometerZ.csv");
        var gyro = ReadTriax(folder, "GyroX.csv", "GyroY.csv", "GyroZ.csv");

        return ImuBuilder.Join(
            accel.Select(a => new ImuBuilder.Triax(a.t, a.x, a.y, a.z)).ToList(),
            gyro.Select(g => new ImuBuilder.Triax(g.t, g.x, g.y, g.z)).ToList());
    }

    /// <summary>True if the folder has any accelerometer/gyro channel files.</summary>
    public static bool HasImu(string folder) =>
        File.Exists(Path.Combine(folder, "AccelerometerX.csv")) || File.Exists(Path.Combine(folder, "GyroX.csv"));

    private static List<(double t, double x, double y, double z)> ReadTriax(string folder, string fx, string fy, string fz)
    {
        var x = ReadTimeValue(Path.Combine(folder, fx));
        var y = ReadTimeValue(Path.Combine(folder, fy));
        var z = ReadTimeValue(Path.Combine(folder, fz));
        int n = Math.Min(x.Count, Math.Min(y.Count, z.Count));
        var list = new List<(double, double, double, double)>(n);
        for (int i = 0; i < n; i++) list.Add((x[i].t, x[i].v, y[i].v, z[i].v));
        return list;
    }

    private static List<(double t, double v)> ReadTimeValue(string path)
    {
        var list = new List<(double, double)>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            int c = line.IndexOf(',');
            if (c <= 0) continue;
            // A numeric first column skips the "time,value" header and the "s,unit" units row.
            if (!double.TryParse(line.AsSpan(0, c), NumberStyles.Any, CultureInfo.InvariantCulture, out double t)) continue;
            if (!double.TryParse(line.AsSpan(c + 1).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v)) continue;
            list.Add((t, v));
        }
        return list;
    }
}
