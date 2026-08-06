using MyChron2Insta360.Core;

namespace MyChron2Insta360.Tests;

/// <summary>
/// The parts of the xrk path that hold without AiM's library present, so they run everywhere. The
/// numbers come from a real MyChron 6 session cross-checked against its RaceStudio CSV export.
/// </summary>
public class XrkMathTests
{
    [Fact]
    public void Gps_week_and_time_of_week_give_exact_utc()
    {
        // Week 2430 began on Sunday 2 August 2026; 58938920 ms into it is 16:22:18.920 GPS time,
        // which is 18 leap seconds ahead of UTC.
        var utc = ItowTime.FromWeek(2430, 58938920);
        Assert.Equal(new DateTime(2026, 8, 2, 16, 22, 0, 920, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Week_number_and_date_hint_agree()
    {
        // The hint-based path is what a CSV export uses; both must land on the same instant.
        var fromWeek = ItowTime.FromWeek(2430, 58938920);
        var fromHint = ItowTime.ToUtc(58938920, new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(fromHint, fromWeek);
    }

    [Fact]
    public void Ecef_converts_to_the_position_the_csv_export_reports()
    {
        // First fix of the reference session. _GPS_o.csv gives 50.83924070, 13.44424159, 559.78624908.
        var (lat, lon, alt) = XrkReader.EcefToGeodetic(3925659.53, 938427.89, 4922704.45);
        Assert.Equal(50.83924070, lat, 7);
        Assert.Equal(13.44424159, lon, 7);
        Assert.Equal(559.786, alt, 2);
    }

    [Fact]
    public void Gyro_is_joined_to_the_nearest_accelerometer_sample()
    {
        // The logger writes the two about 1 ms apart, so an exact match would find nothing.
        var accel = new List<ImuBuilder.Triax>
        {
            new(0.000, 1, 2, 3), new(0.020, 4, 5, 6), new(0.040, 7, 8, 9),
        };
        var gyro = new List<ImuBuilder.Triax>
        {
            new(0.001, 10, 11, 12), new(0.021, 13, 14, 15), new(0.041, 16, 17, 18),
        };
        var imu = ImuBuilder.Join(accel, gyro);

        Assert.Equal(3, imu.Count);
        Assert.Equal(new[] { 10.0, 13.0, 16.0 }, imu.Select(s => s.Gx));
        Assert.Equal(new[] { 1.0, 4.0, 7.0 }, imu.Select(s => s.Ax));
    }

    [Fact]
    public void Missing_gyro_still_yields_accelerometer_samples()
    {
        var imu = ImuBuilder.Join(new List<ImuBuilder.Triax> { new(0, 1, 2, 3) }, new List<ImuBuilder.Triax>());
        Assert.Single(imu);
        Assert.Equal(0, imu[0].Gx);
        Assert.Equal(1, imu[0].Ax);
    }

    [Theory]
    [InlineData("session.xrk", true)]
    [InlineData("session.XRZ", true)]
    [InlineData("session.csv", false)]
    [InlineData("VID_0001.insv", false)]
    public void Logger_files_are_recognised(string name, bool expected) =>
        Assert.Equal(expected, XrkReader.IsXrkFile(name));
}

/// <summary>
/// End to end against a real .xrk with our own parser. Logger files are large and not in the repo, so
/// these run only when MYCHRON2INSTA360_TEST_XRK points at one. No AiM library is needed.
/// </summary>
public class XrkFileTests
{
    private static string? SampleFile
    {
        get
        {
            string? f = Environment.GetEnvironmentVariable("MYCHRON2INSTA360_TEST_XRK");
            return !string.IsNullOrWhiteSpace(f) && File.Exists(f) ? f : null;
        }
    }

    [Fact]
    public void Our_parser_agrees_with_aims_library()
    {
        // Only meaningful when AiM's library is present as a second opinion.
        if (SampleFile is not string file || !XrkReader.DllAvailable) return;

        var mine = XrkReader.Load(file);
        var theirs = XrkReader.LoadViaDll(file);

        Assert.Equal(theirs.Gps.Samples.Count, mine.Gps.Samples.Count);
        Assert.Equal(theirs.Imu.Count, mine.Imu.Count);
        Assert.Equal(theirs.Laps.Count, mine.Laps.Count);
        Assert.Equal(theirs.Info.GpsWeek, mine.Info.GpsWeek);

        for (int i = 0; i < mine.Imu.Count; i += 97)
        {
            Assert.Equal(theirs.Imu[i].ElapsedS, mine.Imu[i].ElapsedS, 9);
            Assert.Equal(theirs.Imu[i].Ax, mine.Imu[i].Ax, 6);
            Assert.Equal(theirs.Imu[i].Gz, mine.Imu[i].Gz, 6);
        }
        for (int i = 0; i < mine.Gps.Samples.Count; i += 89)
        {
            Assert.Equal(theirs.Gps.Samples[i].TimeRel, mine.Gps.Samples[i].TimeRel, 9);
            Assert.Equal(theirs.Gps.Samples[i].Lat, mine.Gps.Samples[i].Lat, 9);
            Assert.Equal(theirs.Gps.Samples[i].Lon, mine.Gps.Samples[i].Lon, 9);
            Assert.Equal(theirs.Gps.Samples[i].ItowMs, mine.Gps.Samples[i].ItowMs, 3);
        }
        // Every channel AiM exposes must be present here and identical.
        foreach (var t in theirs.Channels)
        {
            var m = mine.Channels.FirstOrDefault(c => c.Name == t.Name);
            Assert.True(m is not null, $"channel '{t.Name}' is missing from our parser");
            Assert.Equal(t.Values.Count, m!.Values.Count);
            for (int i = 0; i < m.Values.Count; i += 53) Assert.Equal(t.Values[i], m.Values[i], 5);
        }
    }

    [Fact]
    public void Xrz_reads_the_same_as_xrk()
    {
        if (SampleFile is not string file) return;
        string xrz = Path.ChangeExtension(file, ".xrz");
        if (!File.Exists(xrz)) return;

        var a = XrkReader.Load(file);
        var b = XrkReader.Load(xrz);   // zlib compressed form of the same session
        Assert.Equal(a.Gps.Samples.Count, b.Gps.Samples.Count);
        Assert.Equal(a.Imu.Count, b.Imu.Count);
        Assert.Equal(a.Laps.Count, b.Laps.Count);
        for (int i = 0; i < a.Imu.Count; i += 401) Assert.Equal(a.Imu[i].Ax, b.Imu[i].Ax, 9);
    }

    [Fact]
    public void Computed_channels_are_left_out()
    {
        if (SampleFile is not string file) return;
        var data = XrkReader.Load(file);
        // The logger's own derived set would otherwise become gauges: master clock, lap and predictive
        // times, best-lap differences, alarm thresholds.
        foreach (var junk in new[] { "Master Clk", "Lap Time", "Predictive Time", "Best Run Diff", "Alarm_1_max" })
            Assert.DoesNotContain(data.Channels, c => c.Name == junk);
    }

    [Fact]
    public void Reads_channels_gps_and_laps()
    {
        if (SampleFile is not string file) return;   // library or sample not available here

        var data = XrkReader.Load(file);
        Assert.NotEmpty(data.Imu);
        Assert.NotEmpty(data.Gps.Samples);
        Assert.NotEmpty(data.Laps);

        // Every GPS fix carries a real time of week, and the week number pins it to a date.
        Assert.True(data.Info.GpsWeek > 1024, "the GPS week number should be the full, unwrapped value");
        Assert.All(data.Gps.Samples, s => Assert.True(s.ItowMs > 0));
        Assert.Equal(data.Info.GpsWeek, data.Gps.GpsWeek);

        // Positions must be sane, which also proves the ECEF conversion ran.
        Assert.All(data.Gps.Samples, s =>
        {
            Assert.InRange(s.Lat, -90, 90);
            Assert.InRange(s.Lon, -180, 180);
        });

        // Accelerometer and gyro become the IMU stream, never ordinary channels.
        Assert.DoesNotContain(data.Channels, c => RaceStudioChannelScan.IsImuChannel(c.Name));
        Assert.Contains(data.Imu, s => s.Az != 0);
    }

    [Fact]
    public void Gravity_shows_up_in_the_accelerometer()
    {
        if (SampleFile is not string file) return;

        var data = XrkReader.Load(file);
        // Whatever the mount angle, the vector magnitude must average about 1 g.
        double mean = data.Imu.Average(s => Math.Sqrt(s.Ax * s.Ax + s.Ay * s.Ay + s.Az * s.Az));
        Assert.InRange(mean, 0.8, 1.3);
    }
}
