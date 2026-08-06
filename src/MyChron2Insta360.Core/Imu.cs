namespace MyChron2Insta360.Core;

/// <summary>One inertial sample: 3-axis accelerometer (g) and 3-axis gyroscope (deg/s), on a common
/// timebase. Both the MyChron and Insta360 sources normalize to these units so the IMU CSV is identical.</summary>
public sealed class ImuSample
{
    public double ElapsedS;    // seconds from the start of the recording
    public DateTime? Utc;      // absolute UTC when known (MyChron always; Insta360 after time alignment)
    public double Ax, Ay, Az;  // accelerometer, g
    public double Gx, Gy, Gz;  // gyroscope, deg/s
}

/// <summary>Builds an <see cref="ImuSample"/> stream from separately logged accelerometer and gyroscope
/// series. Shared by the CSV and the xrk readers so both join the two the same way.</summary>
public static class ImuBuilder
{
    public readonly record struct Triax(double T, double X, double Y, double Z);

    /// <summary>
    /// Join the gyroscope onto the accelerometer by nearest timestamp. The logger writes the two on
    /// slightly different grids, about 1 ms apart and each with its own jitter, so an exact timestamp
    /// match misses roughly a third of the gyro samples and repeats the previous one in their place.
    /// Both lists must be in time order, which the sources guarantee.
    /// </summary>
    public static List<ImuSample> Join(IReadOnlyList<Triax> accel, IReadOnlyList<Triax> gyro)
    {
        var imu = new List<ImuSample>(Math.Max(accel.Count, gyro.Count));
        if (accel.Count == 0)
        {
            foreach (var g in gyro)
                imu.Add(new ImuSample { ElapsedS = g.T, Gx = g.X, Gy = g.Y, Gz = g.Z });
            return imu;
        }

        int j = 0;
        foreach (var a in accel)
        {
            Triax g = default;
            if (gyro.Count > 0)
            {
                while (j + 1 < gyro.Count && Math.Abs(gyro[j + 1].T - a.T) <= Math.Abs(gyro[j].T - a.T)) j++;
                g = gyro[j];
            }
            imu.Add(new ImuSample { ElapsedS = a.T, Ax = a.X, Ay = a.Y, Az = a.Z, Gx = g.X, Gy = g.Y, Gz = g.Z });
        }
        return imu;
    }
}
