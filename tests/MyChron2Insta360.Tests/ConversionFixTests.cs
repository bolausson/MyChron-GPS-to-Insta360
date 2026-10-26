using System.Globalization;
using System.Text.RegularExpressions;
using MyChron2Insta360.App;
using MyChron2Insta360.Core;

namespace MyChron2Insta360.Tests;

/// <summary>
/// The rate a downsample delivers. Spacing each kept sample from the last one drifted low whenever the
/// native period did not divide the target one: 25 Hz asked down to 10 Hz kept every third fix, 8.3 Hz.
/// </summary>
public class ThinningTests
{
    private static List<double> Times(double hz, double seconds, double from = 0) =>
        Enumerable.Range(0, (int)Math.Round(hz * seconds)).Select(i => from + i / hz).ToList();

    [Theory]
    [InlineData(25, 10)]
    [InlineData(25, 5)]
    [InlineData(25, 7)]
    [InlineData(50, 20)]
    [InlineData(1000, 50)]
    public void Delivers_the_rate_asked_for(double native, double target)
    {
        var kept = Thinning.ToRate(Times(native, 60), t => t, target);
        Assert.InRange(kept.Count, target * 60 - 1, target * 60 + 1);
    }

    [Fact]
    public void A_gap_does_not_cause_a_burst_after_it()
    {
        var t = Times(25, 2).Concat(Times(25, 2, from: 7)).ToList();   // a 5 s dropout in between
        var kept = Thinning.ToRate(t, x => x, 5);
        Assert.InRange(kept.Count, 19, 21);
        Assert.All(kept.Zip(kept.Skip(1)), p => Assert.True(p.Second - p.First >= 0.2 - 1e-9));
    }

    [Fact]
    public void Asking_for_more_than_the_native_rate_keeps_everything()
    {
        var t = Times(25, 10);
        Assert.Equal(t.Count, Thinning.ToRate(t, x => x, 50).Count);
        Assert.Equal(t.Count, Thinning.ToRate(t, x => x, 0).Count);
    }

    [Fact]
    public void Imu_downsampling_delivers_the_rate_asked_for()
    {
        var imu = Times(50, 60).Select(t => new ImuSample { ElapsedS = t }).ToList();
        Assert.InRange(ImuCsvWriter.Downsample(imu, 20).Count, 1199, 1201);
    }

    [Fact]
    public void The_track_reports_the_rate_it_really_wrote()
    {
        var s = new GpsSession { StartUtc = new DateTime(2026, 10, 24, 14, 41, 51, 800, DateTimeKind.Utc), NativeHz = 25 };
        foreach (var t in Times(25, 20))
            s.Samples.Add(new GpsSample { TimeRel = t, ItowMs = double.NaN, Lat = 50.84, Lon = 13.44 + t * 1e-5,
                                          Alt = 500, SpeedMs = 10, AccuracyM = double.NaN });

        var ten = new ConversionOptions { TargetHz = 10 };
        TrackBuilder.Build(new[] { s }, ten);
        Assert.InRange(ten.TotalPoints, 199, 201);
        Assert.Equal(10, ten.EffectiveHz);
        Assert.True(ten.UsedFileUtc);
        Assert.False(ten.UsedItow);

        var more = new ConversionOptions { TargetHz = 50 };
        TrackBuilder.Build(new[] { s }, more);
        Assert.Equal(500, more.TotalPoints);
        Assert.Equal(25, more.EffectiveHz, 6);   // asked for 50, but the file only has 25
    }
}

/// <summary>
/// Output names. A session name such as "..._a_0242.xrz_SN35004726" has dots of its own, and the
/// command line used to treat the last part as an extension and cut the name there.
/// </summary>
public class OutputNameTests
{
    [Theory]
    [InlineData(@"C:\out\run.gpx", @"C:\out\run")]
    [InlineData(@"C:\out\run.FIT", @"C:\out\run")]
    [InlineData(@"C:\out\run", @"C:\out\run")]
    [InlineData(@"C:\out\Bjoern_72_a_0242.xrz_SN35004726", @"C:\out\Bjoern_72_a_0242.xrz_SN35004726")]
    [InlineData(@"C:\out\run.v2", @"C:\out\run.v2")]
    public void Only_an_output_extension_is_taken_off(string given, string expected) =>
        Assert.Equal(expected, Cli.OutputBase(given));

    [Fact]
    public void A_dotted_session_name_is_kept_whole()
    {
        string vbo = SyntheticVbo.WriteFile(SyntheticVbo.Text(), "Race 1.2 final.vbo");
        string dir = Path.GetDirectoryName(vbo)!;
        try
        {
            Assert.Equal(0, Cli.Run(new[] { vbo, "--format", "both", "--no-ini" }));
            Assert.True(File.Exists(Path.Combine(dir, "Race 1.2 final.gpx")));
            Assert.True(File.Exists(Path.Combine(dir, "Race 1.2 final.fit")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Out_with_an_extension_names_every_output()
    {
        string vbo = SyntheticVbo.WriteFile(SyntheticVbo.Text());
        string dir = Path.GetDirectoryName(vbo)!;
        try
        {
            Assert.Equal(0, Cli.Run(new[] { vbo, "-o", Path.Combine(dir, "run.gpx"), "--format", "both", "--no-ini" }));
            Assert.True(File.Exists(Path.Combine(dir, "run.gpx")));
            Assert.True(File.Exists(Path.Combine(dir, "run.fit")));
            Assert.False(File.Exists(Path.Combine(dir, "run.gpx.gpx")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>
/// The instant a session's IMU, laps and temperature are measured from. The GUI used the first kept
/// fix instead, which a standstill trim moves, and its merge did not stamp the IMU at all.
/// </summary>
public class SessionClockTests
{
    /// <summary>A session as an xrk gives it: GPS week plus iTOW, the first fix 3 s into the logger's run,
    /// standing still for its first second.</summary>
    private static GpsSession XrkLike()
    {
        var s = new GpsSession { GpsWeek = 2441, NativeHz = 25 };
        for (int i = 0; i < 125; i++)
            s.Samples.Add(new GpsSample { TimeRel = 3.0 + i / 25.0, ItowMs = 571_329_800 + i * 40.0,
                                          Lat = 50.84, Lon = 13.44 + i * 1e-6, Alt = 500,
                                          SpeedMs = i < 25 ? 0 : 10, AccuracyM = 0.5 });
        return s;
    }

    [Fact]
    public void Lands_on_the_first_fix_and_carries_the_nudge()
    {
        var s = XrkLike();
        var opt = new ConversionOptions { ManualOffsetSeconds = 1.5 };
        var track = TrackBuilder.Build(new[] { s }, opt);
        DateTime start = TrackBuilder.SessionStartUtc(s, opt)!.Value;
        Assert.Equal(track[0][0].Utc, start.AddSeconds(3.0));
        Assert.Equal(TrackBuilder.SessionStartUtc(s, new ConversionOptions())!.Value.AddSeconds(1.5), start);
    }

    [Fact]
    public void Trimming_the_track_does_not_move_it()
    {
        var s = XrkLike();
        var trimmed = new ConversionOptions { TrimStandstill = true };
        var track = TrackBuilder.Build(new[] { s }, trimmed);
        Assert.True(track[0][0].Utc > TrackBuilder.SessionStartUtc(s, trimmed)!.Value.AddSeconds(3.5));
        Assert.Equal(TrackBuilder.SessionStartUtc(s, new ConversionOptions()), TrackBuilder.SessionStartUtc(s, trimmed));
    }

    [Fact]
    public void A_vbo_session_starts_at_its_stated_utc()
    {
        var s = new GpsSession { StartUtc = new DateTime(2026, 10, 24, 14, 41, 51, 800, DateTimeKind.Utc) };
        Assert.Equal(s.StartUtc.Value.AddSeconds(-2),
                     TrackBuilder.SessionStartUtc(s, new ConversionOptions { ManualOffsetSeconds = -2, LeapSeconds = 17 }));
    }

    [Fact]
    public void A_merged_imu_csv_has_utc_on_every_row()
    {
        using var a = new SyntheticExport(seconds: 20);
        using var b = new SyntheticExport(seconds: 20);
        // Sessions are told apart by name, as RaceStudio names each one uniquely, so rename the second.
        string second = Path.Combine(b.Root, "Second_Session_CSV");
        Directory.Move(b.Folder, second);
        Assert.Equal(0, Cli.Run(new[] { a.Folder, second, "--merge", "--out-dir", a.OutDir, "--no-ini" }));
        string csv = Directory.GetFiles(a.OutDir, "merged-*_imu.csv").Single();
        var rows = File.ReadAllLines(csv).Skip(1).ToList();
        Assert.Equal(2 * 20 * 50, rows.Count);
        Assert.All(rows, r => Assert.False(r.StartsWith(','), "a row without its UTC"));
    }

    [Fact]
    public void A_merged_fit_carries_the_imu()
    {
        // The command line wrote the merged FIT before it had even loaded the IMU, so it held none.
        using var a = new SyntheticExport(seconds: 20);
        using var b = new SyntheticExport(seconds: 20);
        string second = Path.Combine(b.Root, "Second_Session_CSV");
        Directory.Move(b.Folder, second);
        Assert.Equal(0, Cli.Run(new[] { a.Folder, second, "--merge", "--format", "fit", "--no-imu-csv",
                                        "--out-dir", a.OutDir, "--no-ini" }));

        var listener = new Dynastream.Fit.FitListener();
        var decode = new Dynastream.Fit.Decode();
        decode.MesgEvent += listener.OnMesg;
        using (var fs = File.OpenRead(Directory.GetFiles(a.OutDir, "merged-*.fit").Single()))
            Assert.True(decode.Read(fs));
        var accel = listener.FitMessages.AccelerometerDataMesgs;
        Assert.Equal(2 * 20 * 50, accel.Sum(m => m.GetNumSampleTimeOffset()));
    }
}

/// <summary>The command line's own session clock, which IMU, laps and temperature are timed from.</summary>
public class CliClockTests
{
    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static DateTime FirstFix(string gpx) =>
        Utc(Regex.Match(File.ReadAllText(gpx), @"<trkpt[^>]*>\s*<ele>[^<]*</ele>\s*<time>([^<]*)</time>").Groups[1].Value);

    private static List<DateTime> ImuTimes(string csv) =>
        File.ReadLines(csv).Skip(1).Select(r => Utc(r[..r.IndexOf(',')])).ToList();

    [Fact]
    public void The_imu_starts_at_the_session_start_and_moves_with_the_nudge()
    {
        // The first fix comes 2 s after the logger's time zero, which is where the IMU starts.
        using var e = new SyntheticExport(13.0, 20, itowZeroMs: 58938920, gpsDelayS: 2);
        Assert.Equal(0, Cli.Run(new[] { e.Folder, "--nudge", "1.5", "--out-dir", e.OutDir, "--no-ini" }));
        DateTime fix = FirstFix(Path.Combine(e.OutDir, "Synthetic_Session_CSV.gpx"));
        Assert.Equal(fix.AddSeconds(-2), ImuTimes(Path.Combine(e.OutDir, "Synthetic_Session_CSV_imu.csv"))[0]);
    }

    [Fact]
    public void A_merged_imu_csv_keeps_each_session_on_its_own_clock()
    {
        using var a = new SyntheticExport(13.0, 20, itowZeroMs: 58938920, gpsDelayS: 1);
        using var b = new SyntheticExport(13.0, 20, itowZeroMs: 58938920 + 600_000, gpsDelayS: 1);
        string second = Path.Combine(b.Root, "Second_Session_CSV");
        Directory.Move(b.Folder, second);
        Assert.Equal(0, Cli.Run(new[] { a.Folder, second, "--merge", "--out-dir", a.OutDir, "--no-ini" }));
        var utc = ImuTimes(Directory.GetFiles(a.OutDir, "merged-*_imu.csv").Single());
        Assert.Equal(2 * 20 * 50, utc.Count);
        Assert.Equal(utc[0].AddSeconds(600), utc[1000]);   // the second session, ten minutes on
    }

    [Fact]
    public void Merged_clip_imu_is_thinned_on_absolute_time()
    {
        var t0 = new DateTime(2026, 8, 1, 17, 13, 16, DateTimeKind.Utc);
        var imu = Enumerable.Range(0, 10_000)
            .Select(i => new ImuSample { ElapsedS = i / 1000.0, Utc = t0.AddMilliseconds(i) }).ToList();
        imu.Insert(0, new ImuSample());   // no UTC, so it cannot be placed
        var kept = Cli.DownsampleByUtc(imu, 300);
        Assert.InRange(kept.Count, 2999, 3001);
        Assert.All(kept, s => Assert.NotNull(s.Utc));
    }
}

/// <summary>Two recordings of the same stretch cannot share one merged timeline.</summary>
public class MergeOverlapTests
{
    private static readonly DateTime T0 = new(2026, 10, 24, 14, 44, 11, DateTimeKind.Utc);

    private static GpsSession Session(string name, DateTime start, int seconds)
    {
        var s = new GpsSession { Name = name, StartUtc = start, NativeHz = 25 };
        for (int i = 0; i < seconds * 25; i++)
            s.Samples.Add(new GpsSample { TimeRel = i / 25.0, ItowMs = double.NaN, Lat = 50.84, Lon = 13.44 + i * 1e-6,
                                          Alt = 500, SpeedMs = 10, AccuracyM = double.NaN });
        return s;
    }

    [Fact]
    public void Overlapping_recordings_are_reported()
    {
        var opt = new ConversionOptions();
        TrackBuilder.Build(new[] { Session("mychron", T0, 60), Session("dragy", T0.AddSeconds(-20), 100) }, opt);
        Assert.Contains("'dragy' and 'mychron' overlap", Assert.Single(opt.Overlaps));
        Assert.Equal(T0.AddMilliseconds(79_960), opt.EndUtc);   // the later end, not the end of the last to start
    }

    [Fact]
    public void Consecutive_sessions_are_not()
    {
        var opt = new ConversionOptions();
        TrackBuilder.Build(new[] { Session("1", T0, 60), Session("2", T0.AddSeconds(90), 60) }, opt);
        Assert.Empty(opt.Overlaps);
    }

    [Fact]
    public void Imu_on_both_sides_of_a_gap_keeps_its_times_in_the_fit()
    {
        // Ten samples, then ten more ten minutes later. One batch of thirty spans the gap, which a
        // 16-bit millisecond offset cannot reach across: the later ten were clamped to +65.5 s.
        var imu = Enumerable.Range(0, 20).Select(i => new ImuSample
        {
            ElapsedS = i * 0.02,
            Utc = T0.AddMilliseconds((i < 10 ? 0 : 600_000) + i * 20),
            Ax = i,
        }).ToList();
        var track = new List<List<TrackPoint>>
        {
            new() { new TrackPoint { Utc = T0, Lat = 50.84, Lon = 13.44 }, new TrackPoint { Utc = T0.AddSeconds(601), Lat = 50.84, Lon = 13.44 } },
        };
        string path = Path.Combine(Path.GetTempPath(), "m2i-tests", Guid.NewGuid().ToString("N") + ".fit");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            FitWriter.Write(path, track, "gap", new FitWriter.Extras { Imu = imu });
            var listener = new Dynastream.Fit.FitListener();
            var decode = new Dynastream.Fit.Decode();
            decode.MesgEvent += listener.OnMesg;
            using (var fs = File.OpenRead(path)) Assert.True(decode.Read(fs));

            int seen = 0;
            foreach (var m in listener.FitMessages.AccelerometerDataMesgs)
            {
                DateTime t = m.GetTimestamp().GetDateTime().AddMilliseconds(m.GetTimestampMs() ?? 0);
                for (int k = 0; k < m.GetNumSampleTimeOffset(); k++, seen++)
                {
                    var sample = imu[(int)m.GetCalibratedAccelX(k)!.Value];
                    Assert.Equal(sample.Utc!.Value, t.AddMilliseconds(m.GetSampleTimeOffset(k) ?? 0));
                }
            }
            Assert.Equal(20, seen);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>Which GPS columns the Telemetry Overlay CSV carries, and which fixes go into them.</summary>
public class TelemetryOverlayGpsTests
{
    [Fact]
    public void A_mychron_csv_has_no_heading_or_satellites_column()
    {
        using var export = new SyntheticExport(seconds: 10);
        Assert.Equal(0, Cli.Run(new[] { export.Folder, "--format", "telemetry-overlay", "--out-dir", export.OutDir, "--no-ini" }));
        var csv = new AimCsv(export.ExpectedCsv);
        Assert.False(csv.Has("GPS Heading"));
        Assert.False(csv.Has("satellites"));
    }

    [Fact]
    public void Rows_without_a_fix_stay_out_of_the_csv()
    {
        var s = new GpsSession();
        for (int i = 0; i < 50; i++)
            s.Samples.Add(new GpsSample { TimeRel = i / 25.0, ItowMs = double.NaN, Lat = i == 25 ? 0 : 50.84,
                                          Lon = i == 25 ? 0 : 13.44, Alt = 500, SpeedMs = 10, AccuracyM = double.NaN });
        string path = Path.Combine(Path.GetTempPath(), "m2i-tests", Guid.NewGuid().ToString("N") + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            TelemetryOverlayCsv.Write(path, s, null, Array.Empty<ChannelSeries>(),
                                      Array.Empty<(int, double, double)>(), new TelemetryOverlayOptions());
            Assert.All(new AimCsv(path).Column("GPS Latitude"), v => Assert.Equal(50.84, v, 6));
        }
        finally { File.Delete(path); }
    }
}
