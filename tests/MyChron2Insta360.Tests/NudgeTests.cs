using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using MyChron2Insta360.App;
using MyChron2Insta360.Core;

namespace MyChron2Insta360.Tests;

/// <summary>Runs a block under a given Windows number format, and puts the previous one back.</summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _was = CultureInfo.CurrentCulture;
    public CultureScope(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);
    public void Dispose() => CultureInfo.CurrentCulture = _was;
}

/// <summary>Seconds added to a time land on the exact tick, so a time written to the millisecond is right.</summary>
public class ClockTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 15, 17, 10, 160, DateTimeKind.Utc);

    [Fact]
    public void Every_millisecond_nudge_lands_on_its_millisecond()
    {
        // DateTime.AddSeconds missed about every other one of these by a tick.
        for (int ms = -600_000; ms <= 600_000; ms++)
            if (T0.PlusSeconds(ms / 1000.0) != T0.AddTicks(ms * TimeSpan.TicksPerMillisecond))
                Assert.Fail($"{ms} ms came out as {(T0.PlusSeconds(ms / 1000.0) - T0).Ticks} ticks");
    }

    [Fact]
    public void Elapsed_seconds_of_an_hour_long_session_land_on_their_millisecond()
    {
        // The IMU and the laps are stamped as session start plus elapsed seconds, at up to 1 kHz.
        for (int ms = 0; ms <= 3_600_000; ms++)
            if (T0.PlusSeconds(ms / 1000.0) != T0.AddTicks(ms * TimeSpan.TicksPerMillisecond))
                Assert.Fail($"{ms / 1000.0} s came out as {(T0.PlusSeconds(ms / 1000.0) - T0).Ticks} ticks");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void No_number_is_no_time(double seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => T0.PlusSeconds(seconds));
}

/// <summary>GPS time of week to UTC, exact to the millisecond the receiver stamped.</summary>
public class ItowTimeTests
{
    [Fact]
    public void A_fix_from_a_real_export_lands_on_its_millisecond()
    {
        // Bigulia, 2026-07-07: iTOW 232544040 ms. Written as 16:35:26.039 before this was fixed.
        Assert.Equal(new DateTime(2026, 7, 7, 16, 35, 26, 40, DateTimeKind.Utc),
                     ItowTime.ToUtc(232544040, new DateTime(2026, 7, 7)));
    }

    [Fact]
    public void Every_fix_of_a_day_at_25_Hz_lands_on_its_millisecond()
    {
        const int week = 2442;   // late October 2026
        var weekStartUtc = new DateTime(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(7.0 * week).AddSeconds(-18);
        for (long itow = 0; itow < 86_400_000; itow += 40)
            if (ItowTime.FromWeek(week, itow) != weekStartUtc.AddTicks(itow * TimeSpan.TicksPerMillisecond))
                Assert.Fail($"iTOW {itow} came out as {ItowTime.FromWeek(week, itow):HH:mm:ss.fffffff}");
    }

    [Theory]
    // A RaceStudio folder is dated by local time, taken as midnight UTC.
    [InlineData("2026-10-24", "2026-10-25T00:30:00")]   // Saturday 17:30 in California is Sunday in UTC
    [InlineData("2026-10-25", "2026-10-24T22:00:00")]   // Sunday 09:00 in Sydney is still Saturday in UTC
    [InlineData("2026-10-25", "2026-10-24T22:30:00")]   // Sunday 00:30 in Germany in summer
    [InlineData("2026-10-21", "2026-10-21T15:00:00")]   // midweek, where any rule finds the week
    public void A_session_across_the_change_of_gps_week_lands_in_its_own_week(string folderDate, string utc)
    {
        // GPS weeks start on Sunday. Picking the week the date hint fell in put these a whole week off.
        var truth = DateTime.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var epoch = new DateTime(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc);
        double gpsMs = (truth - epoch).TotalMilliseconds + 18_000;
        double itowMs = gpsMs % (7 * 86_400_000.0);
        var hint = DateTime.Parse(folderDate, CultureInfo.InvariantCulture);
        Assert.Equal(truth, ItowTime.ToUtc(itowMs, hint));
    }

    [Theory]
    [InlineData(true)]    // an xrk, which states the GPS week of its first fix
    [InlineData(false)]   // a CSV export, dated by its folder
    public void A_session_through_the_change_of_gps_week_runs_on(bool xrkWeek)
    {
        // 20 s at 25 Hz from 10 s before Sunday 00:00 GPS time, where iTOW starts again from 0.
        var s = new GpsSession { Name = "rollover", NativeHz = 25 };
        if (xrkWeek) s.GpsWeek = 2441; else s.DateHint = new DateTime(2026, 10, 24);
        for (int i = 0; i < 500; i++)
            s.Samples.Add(new GpsSample { TimeRel = i / 25.0, ItowMs = (604_790_000L + i * 40L) % 604_800_000L,
                                          Lat = 50.84, Lon = 13.44 + i * 1e-6, Alt = 500, SpeedMs = 10, AccuracyM = double.NaN });
        var pts = TrackBuilder.Build(new[] { s }, new ConversionOptions()).Single();
        Assert.Equal(500, pts.Count);
        for (int i = 1; i < pts.Count; i++) Assert.Equal(TimeSpan.FromMilliseconds(40), pts[i].Utc - pts[i - 1].Utc);
        Assert.Equal(new DateTime(2026, 10, 24, 23, 59, 32, DateTimeKind.Utc), pts[0].Utc);   // 18 leap seconds
    }

    private static GpsSession CsvSession(DateTime firstUtc, int seconds, DateTime hint, bool fileTime)
    {
        var epoch = new DateTime(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc);
        double gps0 = (firstUtc - epoch).TotalMilliseconds + 18_000;
        var s = new GpsSession { Name = "csv", NativeHz = 25, DateHint = hint, DateHintIsFileTime = fileTime };
        for (int i = 0; i < seconds * 25; i++)
            s.Samples.Add(new GpsSample { TimeRel = 0.354 + i / 25.0, ItowMs = (gps0 + i * 40.0) % 604_800_000.0,
                                          Lat = 50.84, Lon = 13.44 + i * 1e-6, Alt = 500, SpeedMs = 10, AccuracyM = double.NaN });
        return s;
    }

    [Theory]
    // session start (UTC), when the export was written (UTC)
    [InlineData("2026-10-25T09:30:00", "2026-10-28T22:00:00")]   // three and a half days on: was split across two weeks
    [InlineData("2026-10-25T09:30:00", "2026-10-29T12:00:00")]   // four days on: was a week late
    [InlineData("2026-10-19T14:00:00", "2026-10-23T12:00:00")]   // Monday's session written on Friday
    [InlineData("2026-10-24T20:00:00", "2026-10-25T10:00:00")]   // Saturday's written on Sunday, the next GPS week
    [InlineData("2026-07-07T16:35:25.880", "2026-07-14T16:33:00")] // Bigulia: written 6 days 23 h 58 min on
    [InlineData("2026-08-23T16:00:14.560", "2026-08-30T16:30:00")] // a week and 30 min after its start, but not its end
    public void An_export_dated_by_when_it_was_written_lands_on_its_own_day(string start, string written)
    {
        // Outside RaceStudio's dated folders, the only date is the file's write time, after the session.
        var first = DateTime.Parse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var hint = DateTime.Parse(written, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var session = CsvSession(first, 3600, hint, fileTime: true);
        var opt = new ConversionOptions();
        var pts = TrackBuilder.Build(new[] { session }, opt).Single();
        Assert.Equal(first, pts[0].Utc);
        for (int i = 1; i < pts.Count; i++) Assert.Equal(TimeSpan.FromMilliseconds(40), pts[i].Utc - pts[i - 1].Utc);
        Assert.Equal(first.AddSeconds(-0.354), TrackBuilder.SessionStartUtc(session, opt));
    }

    [Fact]
    public void A_folder_date_still_picks_the_nearest_week()
    {
        // A folder date is the day of the session, so a Sunday morning in Sydney, still Saturday in UTC,
        // belongs before it, which a write time could never say.
        var first = new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc);
        var pts = TrackBuilder.Build(new[] { CsvSession(first, 60, new DateTime(2026, 10, 25), fileTime: false) },
                                     new ConversionOptions()).Single();
        Assert.Equal(first, pts[0].Utc);
    }

    [Fact]
    public void An_export_outside_a_dated_folder_is_dated_by_its_write_time()
    {
        string dir = Path.Combine(Path.GetTempPath(), "m2i-tests", Guid.NewGuid().ToString("N"));
        string dated = Path.Combine(dir, "2026-10-24", "Racer", "Session_CSV"), loose = Path.Combine(dir, "Karting", "Session_CSV");
        Directory.CreateDirectory(dated);
        Directory.CreateDirectory(loose);
        try
        {
            string a = Path.Combine(dated, "_GPS_o.csv"), b = Path.Combine(loose, "_GPS_o.csv");
            File.WriteAllText(a, "time, itow\n");
            File.WriteAllText(b, "time, itow\n");
            var written = new DateTime(2026, 10, 28, 22, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(b, written);

            Assert.Equal(new DateTime(2026, 10, 24), RaceStudioGpsCsv.ExtractDateHint(a, out bool fileTimeA));
            Assert.False(fileTimeA);
            Assert.Equal(written, RaceStudioGpsCsv.ExtractDateHint(b, out bool fileTimeB));
            Assert.True(fileTimeB);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_date_hint_and_the_week_agree()
    {
        // Saturday 2026-10-24 is the last day of GPS week 2441, which starts on a Sunday.
        var hint = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        for (long itow = 518_400_000; itow < 518_400_000 + 3_600_000; itow += 40)
            Assert.Equal(ItowTime.FromWeek(2441, itow), ItowTime.ToUtc(itow, hint));
    }
}

/// <summary>A converted session keeps the receiver's own times, with the nudge added exactly.</summary>
public class ExactTimesTests
{
    private static List<string> GpxTimes(string gpx) =>
        Regex.Matches(File.ReadAllText(gpx), "<time>([^<]*)</time>").Select(m => m.Groups[1].Value).Skip(1).ToList();

    private static int Ms(string iso) => int.Parse(iso.Substring(iso.IndexOf('.') + 1, 3), CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("12.345", 345)]
    [InlineData("-3.452", 548)]
    [InlineData("1.7", 700)]
    public void Every_fix_keeps_its_millisecond(string nudge, int expectMs)
    {
        // At 25 Hz from an iTOW on the 40 ms grid, every fix is on that grid, moved only by the nudge.
        using var e = new SyntheticExport(13.0, 30, itowZeroMs: 58938920);
        Assert.Equal(0, Cli.Run(new[] { e.Folder, "--nudge", nudge, "--out-dir", e.OutDir, "--no-imu-csv", "--no-ini" }));
        var times = GpxTimes(Path.Combine(e.OutDir, "Synthetic_Session_CSV.gpx"));
        Assert.Equal(30 * 25, times.Count);
        Assert.All(times, t => Assert.Equal(expectMs % 40, Ms(t) % 40));
    }

    [Fact]
    public void Every_imu_row_is_at_its_own_elapsed_time()
    {
        using var e = new SyntheticExport(13.0, 60, itowZeroMs: 58938920, gpsDelayS: 0.354);
        Assert.Equal(0, Cli.Run(new[] { e.Folder, "--nudge", "2.5", "--out-dir", e.OutDir, "--no-ini" }));
        var rows = File.ReadLines(Path.Combine(e.OutDir, "Synthetic_Session_CSV_imu.csv")).Skip(1)
                       .Select(r => r.Split(',')).ToList();
        DateTime first = DateTime.Parse(rows[0][0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        foreach (var r in rows)
        {
            DateTime utc = DateTime.Parse(r[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            double elapsed = double.Parse(r[1], CultureInfo.InvariantCulture);
            Assert.Equal((long)Math.Round(elapsed * 1000), (long)(utc - first).TotalMilliseconds);
        }
    }
}

/// <summary>The time nudge as the log states it.</summary>
public class NudgeLogTests
{
    [Theory]
    [InlineData("de-DE", -3.452, "-3,452 s, every timestamp moved 3,452 s earlier")]
    [InlineData("en-US", 1.5, "+1.5 s, every timestamp moved 1.5 s later")]
    [InlineData("en-US", -0.0005, "-0.0005 s, every timestamp moved 0.0005 s earlier")]
    [InlineData("en-US", 1.23456789, "+1.2345679 s, every timestamp moved 1.2345679 s later")]   // to the tick, as applied
    [InlineData("en-US", -0.00000004, "none")]   // under half a tick: nothing is applied
    [InlineData("en-US", 0, "none")]
    public void States_the_nudge_applied(string culture, double nudge, string expected)
    {
        using var _ = new CultureScope(culture);
        Assert.Equal(expected, new ConversionOptions { ManualOffsetSeconds = nudge }.DescribeNudge());
    }

    [Fact]
    public void Says_insta360_video_is_not_nudged()
    {
        using var _ = new CultureScope("de-DE");
        Assert.Equal("Note: the time nudge (-3,452 s) applies to MyChron and .vbo sessions, not to Insta360 " +
                     "video, which is written as recorded.", Cli.Insta360NudgeNote(-3.452));
    }
}

/// <summary>An option the command line cannot read stops it, rather than being left out.</summary>
public class CliStrictOptionTests
{
    [Theory]
    [InlineData("en-US", "2,5", 2.5)]
    [InlineData("en-US", "-12,5", -12.5)]
    [InlineData("de-CH", "-3,452", -3.452)]
    [InlineData("de-DE", "-3,452", -3.452)]
    [InlineData("de-DE", "-3.452", -3.452)]
    [InlineData("en-US", "1e3", 1000.0)]
    [InlineData("en-US", " 18 ", 18.0)]
    public void Every_number_takes_a_dot_or_a_comma(string culture, string typed, double expected)
    {
        using var _ = new CultureScope(culture);
        Assert.Equal(expected, Cli.ParseD(typed));
    }

    [Theory]
    [InlineData("1.234,5")]   // both marks: no number here needs a thousands separator
    [InlineData("1,2,3")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("")]
    [InlineData(null)]
    public void Not_a_number(string? typed)
    {
        using var _ = new CultureScope("de-DE");
        Assert.Null(Cli.ParseD(typed));
    }

    [Theory]
    [InlineData("--nudge", "abc")]
    [InlineData("--nudge", "NaN")]
    [InlineData("--hz", "fast")]
    [InlineData("--max-accuracy", "2m")]
    [InlineData("--leap", "x")]
    [InlineData("--imu-hz", "native")]
    [InlineData("--to-rate", "")]
    public void An_unreadable_number_stops_the_run(string option, string value)
    {
        using var e = new SyntheticExport(seconds: 5);
        Assert.Equal(2, Cli.Run(new[] { e.Folder, option, value, "--out-dir", e.OutDir, "--no-ini" }));
        Assert.Empty(Directory.GetFiles(e.OutDir));
    }

    [Theory]
    [InlineData("--nudge")]
    [InlineData("--out-dir")]
    [InlineData("--format")]
    [InlineData("--date")]
    public void A_missing_value_stops_the_run(string option)
    {
        using var e = new SyntheticExport(seconds: 5);
        Assert.Equal(2, Cli.Run(new[] { e.Folder, "--no-ini", option }));
    }

    [Theory]
    [InlineData("--out-dir", "--merge")]   // wrote into a folder called "--merge", and did not merge
    [InlineData("-o", "--fit")]            // wrote "--fit.gpx", and no FIT
    [InlineData("--xrk-dll", "--verify-xrk")]
    [InlineData("--ini", "--merge")]
    [InlineData("-o", "")]                 // an empty variable in a script
    [InlineData("--nudge", "--merge")]
    public void Another_option_where_a_value_belongs_is_a_missing_value(string option, string next)
    {
        using var e = new SyntheticExport(seconds: 5);
        Assert.Equal(2, Cli.Run(new[] { e.Folder, option, next, "--no-ini" }));
    }

    [Fact]
    public void A_settings_file_that_is_not_there_stops_the_run()
    {
        using var e = new SyntheticExport(seconds: 5);
        Assert.Equal(2, Cli.Run(new[] { e.Folder, "--ini", Path.Combine(e.Root, "no-such.ini"), "--out-dir", e.OutDir }));
        Assert.Empty(Directory.GetFiles(e.OutDir));
    }

    [Theory]
    [InlineData("--nuge")]
    [InlineData("--nudge=-3.452")]
    [InlineData("-n")]
    public void An_unknown_option_stops_the_run(string option)
    {
        using var e = new SyntheticExport(seconds: 5);
        Assert.Equal(2, Cli.Run(new[] { e.Folder, option, "-3.452", "--out-dir", e.OutDir, "--no-ini" }));
        Assert.Empty(Directory.GetFiles(e.OutDir));
    }

    [Fact]
    public void The_message_names_the_option_and_what_was_typed()
    {
        Assert.Equal("--nudge needs a number, such as -3.452, not 'abc'.",
                     Assert.Throws<Cli.UsageError>(() => Cli.ReadNumber("--nudge", "abc")).Message);
        Assert.Equal("--nudge needs a number after it, such as -3.452.",
                     Assert.Throws<Cli.UsageError>(() => Cli.ReadNumber("--nudge", null)).Message);
    }

    [Theory]
    [InlineData("de-DE", "-3,452")]
    [InlineData("de-DE", "-3.452")]
    [InlineData("en-US", "-3.452")]
    [InlineData("en-US", "-3,452")]
    [InlineData("de-CH", "-3,452")]
    public void A_negative_nudge_with_either_mark_whatever_the_windows_format(string culture, string typed)
    {
        using var _ = new CultureScope(culture);
        Assert.Equal(-3.452, Cli.ReadNumber("--nudge", typed));
    }
}

/// <summary>Tests that read what the command line prints. Console is process-wide, so they run alone.</summary>
[CollectionDefinition("Console", DisableParallelization = true)]
public class ConsoleCollection { }

[Collection("Console")]
public class CliMessageTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        TextWriter outWas = Console.Out, errWas = Console.Error;
        var o = new StringWriter();
        var e = new StringWriter();
        Console.SetOut(o);
        Console.SetError(e);
        try { return (Cli.Run(args), o.ToString(), e.ToString()); }
        finally { Console.SetOut(outWas); Console.SetError(errWas); }
    }

    [Fact]
    public void A_mistyped_option_is_named()
    {
        using var e = new SyntheticExport(seconds: 5);
        var r = Run(e.Folder, "--nuge", "-3.452", "--out-dir", e.OutDir, "--no-ini");
        Assert.Equal(2, r.Code);
        Assert.Contains("Error: unknown option '--nuge' (--help lists them).", r.Err);
    }

    [Fact]
    public void An_unreadable_nudge_is_named()
    {
        using var e = new SyntheticExport(seconds: 5);
        var r = Run(e.Folder, "--nudge", "abc", "--out-dir", e.OutDir, "--no-ini");
        Assert.Equal(2, r.Code);
        Assert.Contains("Error: --nudge needs a number, such as -3.452, not 'abc'.", r.Err);
    }

    [Fact]
    public void The_log_states_the_nudge_applied()
    {
        using var _ = new CultureScope("en-US");
        using var e = new SyntheticExport(seconds: 5);
        var r = Run(e.Folder, "--nudge", "-3.452", "--out-dir", e.OutDir, "--no-ini");
        Assert.Equal(0, r.Code);
        Assert.Contains("Time nudge: -3.452 s, every timestamp moved 3.452 s earlier" + Environment.NewLine, r.Out);
    }

    [Fact]
    public void A_nudge_from_the_settings_file_says_so_and_a_bad_line_is_reported()
    {
        using var _ = new CultureScope("en-US");
        using var e = new SyntheticExport(seconds: 5);
        string ini = Path.Combine(e.Root, "settings.ini");
        File.WriteAllText(ini, "[MyChron2Insta360]\nnudge = 1.7\nmerge = maybe\n");
        var r = Run(e.Folder, "--out-dir", e.OutDir, "--ini", ini);
        Assert.Equal(0, r.Code);
        Assert.Contains("Time nudge: +1.7 s, every timestamp moved 1.7 s later (from the settings file)", r.Out);
        Assert.Contains($"Warning: {ini}: 'merge = maybe' is not true or false and was ignored.", r.Err);
    }

    [Theory]
    [InlineData("--tilt")]
    [InlineData("--tilt", "auto")]
    public void A_tilt_to_measure_wins_over_an_angle_in_the_settings_file(params string[] tilt)
    {
        using var e = new SyntheticExport(13.0, 20);
        string ini = Path.Combine(e.Root, "settings.ini");
        File.WriteAllText(ini, "[MyChron2Insta360]\ntilt = 5\n");
        var r = Run(new[] { e.Folder, "--out-dir", e.OutDir, "--ini", ini }.Concat(tilt).ToArray());
        Assert.Equal(0, r.Code);
        Assert.DoesNotContain("entered by hand", r.Out);
        Assert.Contains("Tilt applied:  13.0°.", r.Out);
    }

    [Fact]
    public void A_tilt_angle_with_a_comma_on_an_english_system()
    {
        using var _ = new CultureScope("en-US");
        using var e = new SyntheticExport(13.0, 20);
        var r = Run(e.Folder, "--out-dir", e.OutDir, "--no-ini", "--tilt", "-12,5");
        Assert.Equal(0, r.Code);
        Assert.Contains("Tilt applied:  -12.5\u00b0 (entered by hand)", r.Out);
    }

    [Fact]
    public void An_ini_pointer_to_nothing_names_both_files()
    {
        using var e = new SyntheticExport(seconds: 5);
        string stub = Path.Combine(e.Root, "stub.ini"), gone = Path.Combine(e.Root, "gone", "real.ini");
        File.WriteAllText(stub, $"settings-file = {gone}\n");
        var r = Run(e.Folder, "--ini", stub, "--out-dir", e.OutDir);
        Assert.Equal(2, r.Code);
        Assert.Contains($"Error: --ini: '{stub}' points to '{gone}', which is not there.", r.Err);
        Assert.DoesNotContain("built-in defaults", r.Err);
    }

    [Fact]
    public void An_ini_that_is_a_folder_says_so()
    {
        using var e = new SyntheticExport(seconds: 5);
        var r = Run(e.Folder, "--ini", e.OutDir);
        Assert.Equal(2, r.Code);
        Assert.Contains($"Error: --ini: '{e.OutDir}' is a folder, not a settings file.", r.Err);
    }

    [Fact]
    public void A_relative_path_inside_a_dated_folder_keeps_the_folder_date()
    {
        // Run inside the export's own folder, "." has no date in it; the full path does.
        string root = Path.Combine(Path.GetTempPath(), "m2i-tests", Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "2026-10-24", "Racer", "Session_CSV");
        Directory.CreateDirectory(folder);
        string was = Environment.CurrentDirectory;
        try
        {
            File.WriteAllText(Path.Combine(folder, "_GPS_o.csv"), "time, itow\n");
            File.SetLastWriteTimeUtc(Path.Combine(folder, "_GPS_o.csv"), new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc));
            Environment.CurrentDirectory = folder;
            Assert.Equal(new DateTime(2026, 10, 24), RaceStudioGpsCsv.ExtractDateHint(@".\_GPS_o.csv", out bool fileTime));
            Assert.False(fileTime);
        }
        finally
        {
            Environment.CurrentDirectory = was;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_ini_that_cannot_be_read_is_a_usage_error()
    {
        using var e = new SyntheticExport(seconds: 5);
        string ini = Path.Combine(e.Root, "denied.ini");
        File.WriteAllText(ini, "[MyChron2Insta360]\nnudge = 1\n");
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
            System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
        var file = new FileInfo(ini);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(deny);
        file.SetAccessControl(acl);
        try
        {
            var r = Run(e.Folder, "--ini", ini, "--out-dir", e.OutDir);
            Assert.Equal(2, r.Code);
            Assert.Contains($"Error: --ini: '{ini}' cannot be read:", r.Err);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            file.SetAccessControl(acl);
        }
    }

    [Fact]
    public void A_settings_file_that_is_not_there_is_named()
    {
        using var e = new SyntheticExport(seconds: 5);
        string ini = Path.Combine(e.Root, "no-such.ini");
        var r = Run(e.Folder, "--ini", ini, "--out-dir", e.OutDir);
        Assert.Equal(2, r.Code);
        Assert.Contains($"Error: --ini: '{ini}' is not there.", r.Err);
    }

    [Fact]
    public void No_nudge_is_stated_too()
    {
        using var e = new SyntheticExport(seconds: 5);
        var r = Run(e.Folder, "--out-dir", e.OutDir, "--no-ini");
        Assert.Equal(0, r.Code);
        Assert.Contains("Time nudge: none" + Environment.NewLine, r.Out);
    }
}

/// <summary>A settings file line that cannot be read is reported, not quietly dropped.</summary>
public class SettingsProblemTests
{
    private static AppSettings Load(string iniText)
    {
        string dir = Path.Combine(Path.GetTempPath(), "m2i-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ini = Path.Combine(dir, "settings.ini");
            File.WriteAllText(ini, iniText);
            var s = AppSettings.Load(ini, out _, out string? error);
            Assert.Null(error);
            return s;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Each_unreadable_value_is_named_and_ignored()
    {
        var s = Load("[MyChron2Insta360]\nnudge = -3.45.2\nmerge = maybe\nrate = fast\nnuge = -3.452\n" +
                     "trim true\nformat = gpx;fit\nmax-accuracy = 5\n");
        Assert.Equal(new[]
        {
            "'nudge = -3.45.2' is not a number and was ignored.",
            "'merge = maybe' is not true or false and was ignored.",
            "'rate = fast' is not 'native' or a number and was ignored.",
            "'nuge' is not a setting this program knows and was ignored.",
            "'trim true' is not a 'key = value' line and was ignored.",
            "'format = gpx;fit' is not a list of gpx, fit, telemetry-overlay and was ignored.",
        }, s.Problems);
        Assert.Equal(0, s.Nudge);
        Assert.False(s.Merge);
        Assert.True(s.NativeRate);
        Assert.False(s.Trim);
        Assert.Equal("gpx,fit,telemetry-overlay", s.Format);
        Assert.Equal(5, s.MaxAccuracy);
    }

    [Fact]
    public void A_file_the_gui_wrote_has_none()
    {
        var s = Load(new AppSettings { Nudge = -3.452, NativeRate = false, Hz = 10, Tilt = "13.5" }.ToIni());
        Assert.Empty(s.Problems);
        Assert.Equal(-3.452, s.Nudge);
        Assert.Equal(10, s.Hz);
    }

    [Theory]
    [InlineData("on", "auto")]
    [InlineData("yes", "auto")]
    [InlineData("true", "auto")]
    [InlineData("no", "off")]
    [InlineData("false", "off")]
    [InlineData("Auto", "Auto")]
    [InlineData("13.5", "13.5")]
    [InlineData("0", "0")]
    public void Tilt_takes_the_words_every_switch_takes(string written, string read)
    {
        var s = Load($"[MyChron2Insta360]\ntilt = {written}\n");
        Assert.Empty(s.Problems);
        Assert.Equal(read, s.Tilt);
    }

    [Fact]
    public void A_tilt_that_cannot_be_read_stays_on_and_is_measured()
    {
        // Only "off" ever turned it off; "13°" meant on, with an angle the program cannot read.
        var s = Load("[MyChron2Insta360]\ntilt = 13°\n");
        Assert.Equal("auto", s.Tilt);
        Assert.Equal(new[] { "'tilt = 13°' is not off, auto or an angle, so the angle is measured from the data." }, s.Problems);
    }

    [Fact]
    public void A_nudge_with_a_comma_reads_whatever_the_windows_format()
    {
        using var _ = new CultureScope("en-US");
        var s = Load("[MyChron2Insta360]\nnudge = -3,452\n");
        Assert.Empty(s.Problems);
        Assert.Equal(-3.452, s.Nudge);
    }

    [Fact]
    public void A_pointer_named_outright_is_followed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "m2i-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string real = Path.Combine(dir, "real.ini"), stub = Path.Combine(dir, "stub.ini");
            File.WriteAllText(real, "[MyChron2Insta360]\nnudge = 1.7\n");
            File.WriteAllText(stub, $"# This program's settings live elsewhere.\nsettings-file = {real}\n");
            var s = AppSettings.Load(stub, out string used, out string? error);
            Assert.Null(error);
            Assert.Equal(real, used);
            Assert.Equal(1.7, s.Nudge);
            Assert.Empty(s.Problems);

            File.Delete(real);
            AppSettings.Load(stub, out _, out error);
            Assert.Equal($"{stub} points here, but the file is not there. Using the built-in defaults.", error);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_empty_tilt_is_off_on_the_command_line_too()
    {
        var s = Load("[MyChron2Insta360]\ntilt =\n");
        Assert.Empty(s.Problems);
        Assert.Equal("off", s.Tilt);
    }
}

/// <summary>The GUI's number boxes: either decimal mark, and the value used is the value shown.</summary>
public class NumberUpDownTests
{
    [Theory]
    [InlineData("de-DE", "-3,452", 3, "-3.452")]
    [InlineData("de-DE", "-3.452", 3, "-3.452")]
    [InlineData("en-US", "-3,452", 3, "-3.452")]
    [InlineData("en-US", "-3.452", 3, "-3.452")]
    [InlineData("de-CH", "-3,452", 3, "-3.452")]
    [InlineData("fr-FR", "-3.452", 3, "-3.452")]
    [InlineData("sv-SE", "-3,452", 3, "-3.452")]
    [InlineData("de-DE", " 2,5 ", 1, "2.5")]
    [InlineData("de-DE", "1.234,5", 3, null)]
    [InlineData("de-DE", "", 3, null)]
    [InlineData("de-DE", "-", 3, null)]
    [InlineData("de-DE", "abc", 3, null)]
    [InlineData("de-DE", "1.000", 0, "1000")]
    [InlineData("en-US", "1,000", 0, "1000")]
    public void Reads_what_was_typed(string culture, string typed, int decimals, string? expected)
    {
        var ci = new CultureInfo(culture);
        decimal? want = expected is null ? null : decimal.Parse(expected, CultureInfo.InvariantCulture);
        Assert.Equal(want, NumberUpDown.Parse(typed, decimals, ci));
    }

    private static NumberUpDown NudgeBox() => new() { Minimum = -600, Maximum = 600, DecimalPlaces = 3, Increment = 0.5m };

    /// <summary>Text arriving in the edit box as keystrokes do, without the box being told to read it.</summary>
    private static void Type(NumberUpDown box, string text) => box.Controls.OfType<TextBox>().Single().Text = text;

    [Theory]
    [InlineData("-3.452", "-3.452", "-3,452")]
    [InlineData("-3,452", "-3.452", "-3,452")]
    [InlineData("-3,4567", "-3.457", "-3,457")]
    [InlineData("-700", "-600", "-600,000")]
    public void The_value_is_what_the_box_shows(string typed, string value, string shown)
    {
        using var _ = new CultureScope("de-DE");
        using var box = NudgeBox();
        Type(box, typed);
        Assert.Equal(decimal.Parse(value, CultureInfo.InvariantCulture), box.Value);
        Assert.Equal(shown, box.Text);
    }

    [Fact]
    public void An_arrow_reads_the_typing_first()
    {
        using var _ = new CultureScope("de-DE");
        using var box = NudgeBox();
        var seen = new List<decimal>();
        box.ValueChanged += (_, __) => seen.Add(box.Value);
        Type(box, "-3.452");
        box.UpButton();
        Assert.Equal(-2.952m, box.Value);
        Assert.Equal("-2,952", box.Text);
        // Not even for a moment the stock reading, -3452 capped at -600.
        Assert.Equal(new[] { -3.452m, -2.952m }, seen);
    }

    [Fact]
    public void Enter_reads_the_typing_without_a_detour()
    {
        using var _ = new CultureScope("de-DE");
        using var box = NudgeBox();
        var seen = new List<decimal>();
        box.ValueChanged += (_, __) => seen.Add(box.Value);
        Type(box, "-3.452");
        Assert.Equal(-3.452m, box.Value);
        Assert.Equal(new[] { -3.452m }, seen);
    }

    [Theory]
    [InlineData("-599,9996")]   // rounds to the -600 already there
    [InlineData("-700")]        // clamps to it
    public void At_the_limit_an_arrow_still_shows_the_value_in_force(string typed)
    {
        using var _ = new CultureScope("de-DE");
        using var box = NudgeBox();
        box.SetValue(-600);
        Type(box, typed);
        box.DownButton();
        Assert.Equal(-600m, box.Value);
        Assert.Equal("-600,000", box.Text);
    }

    [Fact]
    public void A_decimal_box_at_zero_shows_zero_after_typing_that_rounds_to_it()
    {
        // Max GPS accuracy: "0,04" rounds to 0,0, which means keep every fix, and has to say so.
        using var _ = new CultureScope("de-DE");
        using var box = new NumberUpDown { Minimum = 0, Maximum = 100, DecimalPlaces = 1, Increment = 0.5m };
        Type(box, "0,04");
        box.DownButton();
        Assert.Equal(0m, box.Value);
        Assert.Equal("0,0", box.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("1.234,5")]
    public void Something_not_a_number_shows_the_value_still_in_use(string typed)
    {
        using var _ = new CultureScope("de-DE");
        using var box = NudgeBox();
        box.SetValue(-1.7);
        Type(box, typed);
        Assert.Equal(-1.7m, box.Value);
        Assert.Equal("-1,700", box.Text);
    }

    [Theory]
    [InlineData(-3.4567, "-3.457")]
    [InlineData(9999, "600")]
    [InlineData(double.NaN, "0")]
    public void A_saved_value_is_shown_as_used(double saved, string value)
    {
        using var box = NudgeBox();
        box.SetValue(saved);
        Assert.Equal(decimal.Parse(value, CultureInfo.InvariantCulture), box.Value);
    }

    [Theory]
    [InlineData("fr-FR", '.')]
    [InlineData("sv-SE", '.')]
    [InlineData("de-CH", ',')]
    [InlineData("sv-SE", '-')]
    public void Either_mark_and_a_plain_minus_can_be_typed(string culture, char key)
    {
        // The stock box swallows, with a beep, any mark that is not in Windows' own number format.
        using var _ = new CultureScope(culture);
        using var box = NudgeBox();
        var e = new KeyPressEventArgs(key);
        typeof(NumberUpDown).GetMethod("OnTextBoxKeyPress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(box, new object?[] { null, e });
        Assert.False(e.Handled);
    }
}
