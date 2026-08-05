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

        var imu = new List<ImuSample>(Math.Max(accel.Count, gyro.Count));
        if (accel.Count > 0)
        {
            // RaceStudio writes each channel on its own time grid: gyro typically sits about 1 ms off
            // the accel grid, and both jitter by a sample now and then. Matching timestamps exactly
            // therefore misses roughly a third of the gyro samples and repeats the previous one in
            // their place. Both lists are in time order, so walk them together and take the gyro
            // sample nearest each accel sample.
            int j = 0;
            foreach (var a in accel)
            {
                (double t, double x, double y, double z) g = default;
                if (gyro.Count > 0)
                {
                    while (j + 1 < gyro.Count && Math.Abs(gyro[j + 1].t - a.t) <= Math.Abs(gyro[j].t - a.t)) j++;
                    g = gyro[j];
                }
                imu.Add(new ImuSample { ElapsedS = a.t, Ax = a.x, Ay = a.y, Az = a.z, Gx = g.x, Gy = g.y, Gz = g.z });
            }
        }
        else
        {
            foreach (var g in gyro) imu.Add(new ImuSample { ElapsedS = g.t, Gx = g.x, Gy = g.y, Gz = g.z });
        }
        return imu;
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
