using System.Globalization;
using System.Text;

namespace MyChron2Insta360.Tests;

/// <summary>
/// Builds a throwaway RaceStudio "_CSV" export on disk: GPS at 25 Hz, accelerometer and gyroscope at
/// 50 Hz with a chosen mount tilt baked in, a lap table, and whatever extra channels a test asks for.
/// Having a real folder means the tests exercise channel discovery and the CLI wiring, not just the
/// writer in isolation.
/// </summary>
internal sealed class SyntheticExport : IDisposable
{
    private const double AccelHz = 50, GpsHz = 25;

    public string Root { get; }
    public string Folder { get; }
    public string OutDir { get; }
    public double TiltDeg { get; }

    /// <param name="tiltDeg">Mount tilt to inject, as a rotation about the lateral (Y) axis.</param>
    /// <param name="channels">Extra per-channel files: name, units and a value for each sample.</param>
    public SyntheticExport(double tiltDeg = 13.0, int seconds = 60,
                           params (string Name, string Units, Func<int, double> Value)[] channels)
    {
        TiltDeg = tiltDeg;
        Root = Path.Combine(Path.GetTempPath(), "m2i-tests", Guid.NewGuid().ToString("N"));
        Folder = Path.Combine(Root, "Synthetic_Session_CSV");
        OutDir = Path.Combine(Root, "out");
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(OutDir);

        int n = (int)(seconds * AccelHz);
        double t = tiltDeg * Math.PI / 180.0;
        // A level logger reads gravity on +Z alone; tilting it about Y bleeds a slice onto +X, which
        // is exactly the offset the tilt correction has to remove.
        double gx = Math.Sin(t), gz = Math.Cos(t);
        var jitter = new Random(20260805);
        double J() => (jitter.NextDouble() - 0.5) * 0.004;   // stays inside the "steady sample" window

        WriteChannel("AccelerometerX", "g", n, i => gx + J());
        WriteChannel("AccelerometerY", "g", n, _ => J());
        WriteChannel("AccelerometerZ", "g", n, _ => gz + J());
        WriteChannel("GyroX", "deg/s", n, _ => J());
        WriteChannel("GyroY", "deg/s", n, _ => J());
        WriteChannel("GyroZ", "deg/s", n, _ => J());
        foreach (var (name, units, value) in channels) WriteChannel(name, units, n, value);

        WriteGps(seconds);
        WriteLaps(seconds);
    }

    private void WriteChannel(string name, string units, int n, Func<int, double> value)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("time, value\n").Append("s, ").Append(units).Append('\n');
        for (int i = 0; i < n; i++)
            sb.Append((i / AccelHz).ToString("0.000000", ci)).Append(", ")
              .Append(value(i).ToString("0.####", ci)).Append('\n');
        File.WriteAllText(Path.Combine(Folder, name + ".csv"), sb.ToString());
    }

    private void WriteGps(int seconds)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("time, itow, lat, lon, alt, speed, accuracy\n");
        int n = (int)(seconds * GpsHz);
        for (int i = 0; i < n; i++)
        {
            double t = i / GpsHz;
            double speed = 20 + 5 * Math.Sin(t / 5);                 // m/s, so the km/h scaling is visible
            sb.Append(t.ToString("0.000000", ci)).Append(", ")
              .Append((58938920 + (long)(t * 1000)).ToString(ci)).Append(", ")
              .Append((50.839 + t * 1e-5).ToString("0.00000000", ci)).Append(", ")
              .Append((13.444 + t * 1e-5).ToString("0.00000000", ci)).Append(", ")
              .Append((560 + Math.Sin(t / 10)).ToString("0.0000", ci)).Append(", ")
              .Append(speed.ToString("0.000", ci)).Append(", 0.500\n");
        }
        File.WriteAllText(Path.Combine(Folder, "_GPS_o.csv"), sb.ToString());
    }

    private void WriteLaps(int seconds)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("run, lap, split, start, time\n#, #, #, s, s\n");
        double lapLen = seconds / 3.0;
        for (int lap = 1; lap <= 3; lap++)
            sb.Append("1, ").Append(lap.ToString(ci)).Append(", 1, ")
              .Append(((lap - 1) * lapLen).ToString("0.000", ci)).Append(", ")
              .Append(lapLen.ToString("0.000", ci)).Append('\n');
        File.WriteAllText(Path.Combine(Folder, "_laps_and_splits.csv"), sb.ToString());
    }

    /// <summary>The Telemetry Overlay CSV this export produces, once written.</summary>
    public string ExpectedCsv => Path.Combine(OutDir, "Synthetic_Session_CSV_TelemetryOverlay.csv");

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* a locked temp dir is not a test failure */ }
    }
}
