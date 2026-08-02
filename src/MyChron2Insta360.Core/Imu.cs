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
