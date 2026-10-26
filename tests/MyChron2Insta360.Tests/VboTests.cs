using MyChron2Insta360.App;
using MyChron2Insta360.Core;

namespace MyChron2Insta360.Tests;

/// <summary>
/// Reading a .vbo: the three conventions that decide where a track lands (minutes, longitude positive
/// West, UTC time of day with the date only in the header), and everything a real file throws at it.
/// </summary>
public class VboReaderTests
{
    private static VboData Parse(string text, string name = "dragylap_20261024_164151_Synthetic.vbo",
                                 DateTime? date = null) =>
        VboReader.Parse(new StringReader(text), name, date);

    [Fact]
    public void Reads_a_dragy_file()
    {
        var d = Parse(SyntheticVbo.Text(seconds: 10));
        var g = d.Gps;
        Assert.Equal(250, g.Samples.Count);
        Assert.Equal(new DateTime(2026, 10, 24, 14, 41, 51, 800, DateTimeKind.Utc), g.StartUtc);
        Assert.Equal(25.0, g.NativeHz, 6);

        var s = g.Samples[0];
        Assert.Equal(SyntheticVbo.Lat0, s.Lat, 9);
        Assert.Equal(SyntheticVbo.Lon0, s.Lon, 9);       // East, although the file says -806.7
        Assert.Equal(40 / 3.6, s.SpeedMs, 9);             // km/h in the file, m/s here
        Assert.Equal(350.0, s.HeadingDeg, 9);
        Assert.Equal(519.9, s.Alt, 9);
        Assert.Equal(25.0, s.Satellites);
        Assert.True(double.IsNaN(s.ItowMs));
        Assert.True(double.IsNaN(s.AccuracyM));

        Assert.Equal("dragy Lap", d.Info.Writer);
        Assert.Equal("dragy DRG73", d.Info.Model);
        Assert.Equal("'UTC Date Started'", d.Info.DateSource);
        Assert.Equal(0, d.Info.SkippedRows);
    }

    [Theory]
    [InlineData("-806.714640", 13.445244)]     // Dragy: no padding, East is negative
    [InlineData("+00058.08744", -0.968124)]    // Racelogic's own padded example, West is positive
    public void Longitude_counts_positive_west(string longMinutes, double expectedDeg)
    {
        var d = Parse(SyntheticVbo.Minimal(SyntheticVbo.Columns, new[]
        {
            $"009 100000.00 +03050.395860 {longMinutes} 012.345 090.00 +00100.00",
            $"009 100000.10 +03050.395860 {longMinutes} 012.345 090.00 +00100.00",
        }));
        Assert.Equal(expectedDeg, d.Gps.Samples[0].Lon, 6);
    }

    [Theory]
    [InlineData("144151.840", 52_911_840)]    // read as a double this is ...839.999
    [InlineData("120000.10", 43_200_100)]     // two decimals, as 10 Hz loggers write it
    [InlineData("94151.80", 34_911_800)]      // a leading zero dropped
    [InlineData("000000.000", 0)]
    [InlineData("+144151.8", 52_911_800)]
    public void Time_of_day_is_read_from_its_digits(string token, long expectedMs)
    {
        Assert.True(VboReader.TryTimeOfDayMs(token, out long ms));
        Assert.Equal(expectedMs, ms);
    }

    [Theory]
    [InlineData("146151.000")]   // 61 minutes: seconds since midnight, not a time of day
    [InlineData("1441518.000")]
    [InlineData("14:41:51")]
    [InlineData("")]
    public void Anything_but_a_time_of_day_is_refused(string token) =>
        Assert.False(VboReader.TryTimeOfDayMs(token, out _));

    [Fact]
    public void Midnight_rolls_into_the_next_day()
    {
        var d = Parse(SyntheticVbo.Text(seconds: 2, startMs: 86_399_000));   // 23:59:59.000
        var g = d.Gps;
        Assert.Equal(new DateTime(2026, 10, 24, 23, 59, 59, DateTimeKind.Utc), g.StartUtc);
        Assert.Equal(1, d.Info.MidnightRollovers);
        Assert.Equal(50, g.Samples.Count);
        Assert.Equal(1.0, g.Samples[25].TimeRel, 9);       // 00:00:00.000 the next day
        Assert.Equal(1.96, g.Samples[^1].TimeRel, 9);
    }

    [Fact]
    public void Repeats_and_backward_steps_are_skipped()
    {
        var d = Parse(SyntheticVbo.Minimal(SyntheticVbo.Columns, new[]
        {
            SyntheticVbo.Row("100000.000"), SyntheticVbo.Row("100000.040"),
            SyntheticVbo.Row("100000.040"), SyntheticVbo.Row("100000.020"),   // a repeat, then a step back
            SyntheticVbo.Row("100000.080"), "025 garbage", "",
        }));
        Assert.Equal(new[] { 0.0, 0.04, 0.08 }, d.Gps.Samples.Select(s => Math.Round(s.TimeRel, 6)));
        Assert.Equal(3, d.Info.SkippedRows);
    }

    [Fact]
    public void Crlf_and_lf_read_the_same()
    {
        var lf = Parse(SyntheticVbo.Text()).Gps;
        var crlf = Parse(SyntheticVbo.Text(newline: "\r\n")).Gps;
        Assert.Equal(lf.Samples.Count, crlf.Samples.Count);
        Assert.Equal(lf.StartUtc, crlf.StartUtc);
        Assert.Equal(lf.Samples[^1].Lat, crlf.Samples[^1].Lat);
        Assert.Equal(lf.Samples[^1].TimeRel, crlf.Samples[^1].TimeRel);
    }

    [Fact]
    public void A_racelogic_file_with_extra_columns_and_sections_reads()
    {
        string text =
            "File created on 07/12/2014 @ 09:02:23\r\n\r\n" +
            "[header]\r\nsatellites\r\ntime\r\nlatitude\r\nlongitude\r\nvelocity kmh\r\nheading\r\nheight\r\n" +
            "vertical velocity kmh\r\navifileindex\r\navisynctime\r\n\r\n" +
            "[channel units]\r\n\r\n[comments]\r\nVBox II\r\n\r\n" +
            "[laptiming]\r\nStart   +000058.12345 +003119.12345 +000058.12346 +003119.12346 ¬ Start / Finish\r\n\r\n" +
            "[avi]\r\nVBOX\r\nMP4\r\n\r\n" +
            "[column names]\r\nsats time lat long velocity heading height vert-vel avifileindex avisynctime\r\n\r\n" +
            "[data]\r\n" +
            "137 090223.00 +03119.09973 +00058.08744 012.345 090.00 +00100.00 +000.123 0001 00012345\r\n" +
            "137 090223.10 +03119.09974 +00058.08745 012.400 090.10 +00100.10 +000.120 0001 00012445\r\n";
        var d = Parse(text, "VBOX0001.vbo");
        var g = d.Gps;
        Assert.Equal(2, g.Samples.Count);
        Assert.Equal(new DateTime(2014, 12, 7, 9, 2, 23, DateTimeKind.Utc), g.StartUtc);
        Assert.Equal("'File created on'", d.Info.DateSource);
        Assert.Equal(3119.09973 / 60, g.Samples[0].Lat, 9);
        Assert.Equal(-58.08744 / 60, g.Samples[0].Lon, 9);
        Assert.Equal(9.0, g.Samples[0].Satellites);      // 137 = 128 (DGPS) + 9
        Assert.Equal(12.345 / 3.6, g.Samples[0].SpeedMs, 9);
        Assert.Equal(0.1, g.Samples[1].TimeRel, 9);
    }

    [Theory]
    [InlineData("velocity kmh", 1 / 3.6)]
    [InlineData("velocity mph", 0.44704)]
    [InlineData("velocity knots", 1852.0 / 3600.0)]
    [InlineData("velocity m/s", 1.0)]
    public void Speed_follows_the_unit_in_the_header(string header, double toMs)
    {
        var d = Parse(SyntheticVbo.Minimal(SyntheticVbo.Columns,
            new[] { SyntheticVbo.Row("100000.00"), SyntheticVbo.Row("100000.10") }, header));
        Assert.Equal(21.816 * toMs, d.Gps.Samples[0].SpeedMs, 9);
    }

    [Fact]
    public void A_file_without_gps_columns_is_refused()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            Parse("[column names]\nsats rpm\n\n[data]\n025 1234\n"));
        Assert.Contains("not a VBO GPS log", e.Message);
    }

    [Theory]
    [InlineData("File created on 24/10/2026 @ 14:41")]
    [InlineData("File created on 24/10/2026 at 14:41:51")]
    [InlineData("File created on 24/10/2026")]
    public void File_created_on_dates_a_file_without_the_utc_line(string firstLine)
    {
        var d = Parse(firstLine + "\n\n" + SyntheticVbo.Text(utcStarted: false, created: false), "plain.vbo");
        Assert.Equal(new DateTime(2026, 10, 24, 14, 41, 51, 800, DateTimeKind.Utc), d.Gps.StartUtc);
        Assert.Equal("'File created on'", d.Info.DateSource);
    }

    [Fact]
    public void The_file_name_is_the_last_resort()
    {
        // Dragy names files in local time: 16:41 CEST for a first fix at 14:41 UTC, the same day.
        var d = Parse(SyntheticVbo.Text(utcStarted: false, created: false), "dragylap_20261024_164151_Track.vbo");
        Assert.Equal(new DateTime(2026, 10, 24, 14, 41, 51, 800, DateTimeKind.Utc), d.Gps.StartUtc);
        Assert.Equal("the file name", d.Info.DateSource);
    }

    [Fact]
    public void A_local_stamp_after_midnight_belongs_to_the_utc_day_before()
    {
        // 01:30 CEST on the 25th is 23:30 UTC on the 24th.
        var d = Parse(SyntheticVbo.Text(startMs: 84_600_000, utcStarted: false, created: false), "run_20261025_013000.vbo");
        Assert.Equal(new DateTime(2026, 10, 24, 23, 30, 0, DateTimeKind.Utc), d.Gps.StartUtc);
    }

    [Fact]
    public void A_utc_stamp_just_after_midnight_keeps_a_first_fix_just_before_it()
    {
        string text = SyntheticVbo.Text(startMs: 86_399_800, date: "25/10/2026").Replace("25/10/2026 23:59", "25/10/2026 00:00");
        var d = Parse(text);
        Assert.Equal(new DateTime(2026, 10, 24, 23, 59, 59, 800, DateTimeKind.Utc), d.Gps.StartUtc);
    }

    [Fact]
    public void Without_any_date_it_asks_for_one()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            Parse(SyntheticVbo.Text(utcStarted: false, created: false), "plain.vbo"));
        Assert.Contains("--date", e.Message);
    }

    [Fact]
    public void A_given_date_fills_in_only_where_the_header_has_none()
    {
        // A header date is the recorder's own word, so it stays; --date beats only a file name's hint.
        var dated = Parse(SyntheticVbo.Text(), date: new DateTime(2026, 10, 1));
        Assert.Equal(new DateTime(2026, 10, 24, 14, 41, 51, 800, DateTimeKind.Utc), dated.Gps.StartUtc);

        var named = Parse(SyntheticVbo.Text(utcStarted: false, created: false), date: new DateTime(2026, 10, 1));
        Assert.Equal(new DateTime(2026, 10, 1, 14, 41, 51, 800, DateTimeKind.Utc), named.Gps.StartUtc);
        Assert.Equal("--date", named.Info.DateSource);
    }

    [Fact]
    public void File_created_on_comes_before_the_file_name()
    {
        var d = Parse("File created on 23/10/2026 @ 14:41\n\n" + SyntheticVbo.Text(utcStarted: false, created: false),
                      "dragylap_20261024_164151_Track.vbo");
        Assert.Equal(new DateTime(2026, 10, 23, 14, 41, 51, 800, DateTimeKind.Utc), d.Gps.StartUtc);
        Assert.Equal("'File created on'", d.Info.DateSource);
    }

    [Theory]
    [InlineData("Run_2026-10-25.vbo")]          // a local date only
    [InlineData("run_20261025_0130.vbo")]       // hours and minutes, no seconds
    public void A_local_name_just_after_midnight_belongs_to_the_utc_day_before(string name)
    {
        // 23:30 UTC at the Erzgebirgsring is 01:30 local time on the next day.
        var d = Parse(SyntheticVbo.Text(startMs: 84_600_000, utcStarted: false, created: false), name);
        Assert.Equal(new DateTime(2026, 10, 24, 23, 30, 0, DateTimeKind.Utc), d.Gps.StartUtc);
    }

    [Theory]
    [InlineData("File created on 01/01/0000 @ 00:00")]   // an unset clock
    [InlineData("File created on 01/01/1970 @ 00:00")]
    public void A_placeholder_header_date_is_no_date(string firstLine)
    {
        var d = Parse(firstLine + "\n\n" + SyntheticVbo.Text(utcStarted: false, created: false),
                      "dragylap_20261024_164151_Track.vbo");
        Assert.Equal("the file name", d.Info.DateSource);
        Assert.Equal(new DateTime(2026, 10, 24, 14, 41, 51, 800, DateTimeKind.Utc), d.Gps.StartUtc);
    }

    [Fact]
    public void A_serial_number_after_the_date_does_not_lose_the_date()
    {
        var d = Parse(SyntheticVbo.Text(utcStarted: false, created: false), "track_20261024_35004726.vbo");
        Assert.Equal(new DateTime(2026, 10, 24, 14, 41, 51, 800, DateTimeKind.Utc), d.Gps.StartUtc);
    }

    [Theory]
    [InlineData("run.vbo", true, false)]
    [InlineData("RUN.VBO", true, false)]
    [InlineData("run.vbox", false, false)]
    [InlineData("Erzgebirgsring  1_2026_10_24_16_41_51.dlap", false, true)]
    public void Files_are_recognised_by_extension(string name, bool vbo, bool dlap)
    {
        Assert.Equal(vbo, VboReader.IsVboFile(name));
        Assert.Equal(dlap, VboReader.IsDlapFile(name));
    }
}

/// <summary>
/// A stray row, whatever time it carries, must not take the rest of the file with it. Judged only
/// against the row before, one row an hour ahead dropped everything after it, and one at 00:00 in an
/// afternoon session moved everything after it to the next day.
/// </summary>
public class VboTimelineTests
{
    private const long Ten = 36_000_000;   // 10:00:00 UTC

    private static VboData Parse(params string[] rows) =>
        VboReader.Parse(new StringReader(SyntheticVbo.Minimal(SyntheticVbo.Columns, rows)), "t.vbo", null);

    private static string[] Rows(long startMs, int count, long stepMs = 100) =>
        Enumerable.Range(0, count).Select(i => SyntheticVbo.Row(SyntheticVbo.Time((startMs + i * stepMs) % 86_400_000))).ToArray();

    [Fact]
    public void A_stray_row_far_ahead_is_dropped_not_followed()
    {
        var rows = Rows(Ten, 60);
        rows[20] = SyntheticVbo.Row("150000.00");
        var d = Parse(rows);
        Assert.Equal(59, d.Gps.Samples.Count);
        Assert.Equal(5.9, d.Gps.Samples[^1].TimeRel, 6);
        Assert.Equal(1, d.Info.SkippedRows);
        Assert.Equal(0, d.Info.MidnightRollovers);
    }

    [Fact]
    public void A_stray_midnight_row_is_not_a_new_day()
    {
        var rows = Rows(SyntheticVbo.Start, 60).ToList();
        rows.Insert(30, SyntheticVbo.Row("000000.000"));
        var d = Parse(rows.ToArray());
        Assert.Equal(60, d.Gps.Samples.Count);
        Assert.Equal(0, d.Info.MidnightRollovers);
        Assert.Equal(5.9, d.Gps.Samples[^1].TimeRel, 6);
    }

    [Fact]
    public void A_first_row_out_of_step_does_not_set_the_clock()
    {
        var d = Parse(Rows(Ten, 30).Prepend(SyntheticVbo.Row("100100.00")).ToArray());   // a minute ahead
        Assert.Equal(30, d.Gps.Samples.Count);
        Assert.Equal(new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc), d.Gps.StartUtc);
    }

    [Fact]
    public void Rows_without_a_fix_are_left_out()
    {
        const string noFix = "000 000000.000 +0000.000000 +0000.000000 000.000 000.00 +00000.00";
        var d = Parse(Rows(Ten, 30).Prepend(noFix).ToArray());
        Assert.Equal(30, d.Gps.Samples.Count);
        Assert.Equal(new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc), d.Gps.StartUtc);
    }

    [Fact]
    public void A_stray_row_behind_a_good_one_does_not_replace_it()
    {
        var d = Parse(SyntheticVbo.Row("100000.000"), SyntheticVbo.Row("100000.040"), SyntheticVbo.Row("100000.020"),
                      SyntheticVbo.Row("100000.080"), SyntheticVbo.Row("100000.120"));
        Assert.Equal(new[] { 0.0, 0.04, 0.08, 0.12 }, d.Gps.Samples.Select(s => Math.Round(s.TimeRel, 6)));
    }

    [Fact]
    public void A_leap_second_keeps_time_moving_forward()
    {
        var d = Parse(SyntheticVbo.Row("235959.500"), SyntheticVbo.Row("235960.000"), SyntheticVbo.Row("235960.500"),
                      SyntheticVbo.Row("000000.000"), SyntheticVbo.Row("000000.500"));
        var t = d.Gps.Samples.Select(s => s.TimeRel).ToList();
        Assert.True(t.Zip(t.Skip(1)).All(p => p.Second > p.First), string.Join(", ", t));
        Assert.Equal(1, d.Info.MidnightRollovers);
    }

    [Fact]
    public void One_fix_is_no_track() =>
        Assert.Throws<InvalidDataException>(() => Parse(SyntheticVbo.Row("100000.000")));
}

/// <summary>How .vbo files sit among the other inputs, and what a .dlap gets told.</summary>
public class VboDiscoveryTests
{
    [Fact]
    public void An_unreadable_vbo_beside_a_session_is_skipped_with_a_note()
    {
        using var export = new SyntheticExport(seconds: 10);
        File.WriteAllText(Path.Combine(export.Root, "broken.vbo"), "not a vbo at all");
        var notes = new List<string>();
        var sources = MyChronSources.LoadAll(new[] { export.Root }, log: notes.Add);
        Assert.Equal("CSV", Assert.Single(sources).SourceKind);
        Assert.Contains(notes, n => n.Contains("skipping 'broken.vbo'"));
    }

    [Fact]
    public void An_unreadable_vbo_on_its_own_is_an_error()
    {
        string vbo = SyntheticVbo.WriteFile("not a vbo at all", "broken.vbo");
        try { Assert.Throws<InvalidDataException>(() => MyChronSources.LoadAll(new[] { vbo })); }
        finally { Directory.Delete(Path.GetDirectoryName(vbo)!, recursive: true); }
    }

    [Fact]
    public void A_vbo_reached_twice_is_one_session()
    {
        string vbo = SyntheticVbo.WriteFile(SyntheticVbo.Text());
        string dir = Path.GetDirectoryName(vbo)!;
        try { Assert.Single(MyChronSources.Discover(new[] { dir.Replace('\\', '/'), vbo })); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_folder_with_a_vbo_and_an_export_yields_both()
    {
        using var export = new SyntheticExport();
        File.WriteAllText(Path.Combine(export.Root, "friend.vbo"), SyntheticVbo.Text());
        var found = MyChronSources.Discover(new[] { export.Root });
        Assert.Equal(new[] { "CSV", "vbo" }, found.Select(c => c.Kind));
    }

    [Fact]
    public void A_vbo_inside_a_csv_folder_is_still_its_own_session()
    {
        using var export = new SyntheticExport();
        string vbo = Path.Combine(export.Folder, "friend.vbo");
        File.WriteAllText(vbo, SyntheticVbo.Text());
        Assert.Equal(new[] { "CSV", "vbo" }, MyChronSources.Discover(new[] { export.Root }).Select(c => c.Kind));
        var alone = Assert.Single(MyChronSources.Discover(new[] { vbo }));
        Assert.True(alone.IsVbo);
        Assert.Equal("friend", alone.BaseName);
    }

    [Fact]
    public void A_dlap_on_its_own_says_what_to_do()
    {
        using var export = new SyntheticExport();
        string dlap = Path.Combine(export.OutDir, "Erzgebirgsring  1_2026_10_24_16_41_51.dlap");
        File.WriteAllText(dlap, "");
        foreach (var input in new[] { dlap, export.OutDir })
        {
            var e = Assert.Throws<FileNotFoundException>(() => MyChronSources.Discover(new[] { input }));
            Assert.StartsWith("A Dragy .dlap cannot be read: export the session as .vbo", e.Message);
        }
    }

    [Fact]
    public void A_dlap_beside_something_readable_is_left_out()
    {
        using var export = new SyntheticExport();
        string dlap = Path.Combine(export.OutDir, "session.dlap");
        string vbo = Path.Combine(export.OutDir, "session.vbo");
        File.WriteAllText(dlap, "");
        File.WriteAllText(vbo, SyntheticVbo.Text());
        Assert.True(Assert.Single(MyChronSources.Discover(new[] { dlap, vbo })).IsVbo);
    }
}

/// <summary>A .vbo through the real command line, end to end.</summary>
public class VboCliTests
{
    private static string Stem(string vbo, string dir) => Path.Combine(dir, Path.GetFileNameWithoutExtension(vbo));

    private static string FirstTime(string gpx) =>
        System.Text.RegularExpressions.Regex.Match(File.ReadAllText(gpx), "<trkpt[^>]*>\\s*<ele>[^<]*</ele>\\s*<time>([^<]*)</time>").Groups[1].Value;

    [Fact]
    public void Converts_to_gpx_fit_and_telemetry_overlay()
    {
        string vbo = SyntheticVbo.WriteFile(SyntheticVbo.Text(seconds: 20));
        string dir = Path.GetDirectoryName(vbo)!, outDir = Path.Combine(dir, "out");
        try
        {
            Assert.Equal(0, Cli.Run(new[] { vbo, "--format", "gpx,fit,telemetry-overlay", "--out-dir", outDir, "--no-ini" }));
            string stem = Stem(vbo, outDir);
            Assert.True(File.Exists(stem + ".gpx"));
            Assert.True(File.Exists(stem + ".fit"));
            Assert.True(File.Exists(stem + "_TelemetryOverlay.csv"));
            Assert.False(File.Exists(stem + "_imu.csv"));   // a .vbo has no IMU

            Assert.Equal("2026-10-24T14:41:51.800Z", FirstTime(stem + ".gpx"));
            Assert.Contains("<gpxtpx:course>350</gpxtpx:course>", File.ReadAllText(stem + ".gpx"));

            var to = new AimCsv(stem + "_TelemetryOverlay.csv");
            Assert.Equal("deg", to.UnitOf("GPS Heading"));
            Assert.Equal("sats", to.UnitOf("satellites"));
            Assert.Empty(to.BeaconMarkers());
            // The heading crosses north. Interpolated the long way round, rows would sit near 180.
            Assert.All(to.Column("GPS Heading"), h => Assert.True(h < 30 || h > 349, $"heading {h} is off the course"));
            Assert.Equal(40.0, to.Column("GPS Speed")[0], 3);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Without_an_output_folder_the_files_go_beside_the_vbo()
    {
        string vbo = SyntheticVbo.WriteFile(SyntheticVbo.Text());
        string dir = Path.GetDirectoryName(vbo)!;
        try
        {
            Assert.Equal(0, Cli.Run(new[] { vbo, "--no-ini" }));
            Assert.True(File.Exists(Stem(vbo, dir) + ".gpx"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Nudge_moves_a_vbo_and_leap_seconds_do_not()
    {
        string vbo = SyntheticVbo.WriteFile(SyntheticVbo.Text());
        string dir = Path.GetDirectoryName(vbo)!;
        try
        {
            Assert.Equal(0, Cli.Run(new[] { vbo, "--nudge", "2", "--no-ini" }));
            Assert.Equal("2026-10-24T14:41:53.800Z", FirstTime(Stem(vbo, dir) + ".gpx"));
            Assert.Equal(0, Cli.Run(new[] { vbo, "--leap", "17", "--no-ini" }));
            Assert.Equal("2026-10-24T14:41:51.800Z", FirstTime(Stem(vbo, dir) + ".gpx"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_vbo_without_a_date_takes_it_from_the_command_line()
    {
        string vbo = SyntheticVbo.WriteFile(SyntheticVbo.Text(utcStarted: false, created: false), "plain.vbo");
        string dir = Path.GetDirectoryName(vbo)!;
        try
        {
            Assert.Equal(1, Cli.Run(new[] { vbo, "--no-ini" }));
            Assert.Equal(0, Cli.Run(new[] { vbo, "--date", "2026-10-24", "--no-ini" }));
            Assert.Equal("2026-10-24T14:41:51.800Z", FirstTime(Stem(vbo, dir) + ".gpx"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>
/// End to end against a real .vbo. Such files are not in the repo, so this runs only when
/// MYCHRON2INSTA360_TEST_VBO points at one.
/// </summary>
public class VboFileTests
{
    private static string? SampleFile
    {
        get
        {
            string? f = Environment.GetEnvironmentVariable("MYCHRON2INSTA360_TEST_VBO");
            return !string.IsNullOrWhiteSpace(f) && File.Exists(f) ? f : null;
        }
    }

    [Fact]
    public void A_real_file_reads_cleanly_and_converts_onto_its_own_clock()
    {
        if (SampleFile is not string file) return;

        var data = VboReader.Load(file);
        var g = data.Gps;
        Assert.NotEmpty(g.Samples);
        Assert.NotNull(g.StartUtc);
        Assert.Equal(0, data.Info.SkippedRows);
        Assert.All(g.Samples.Zip(g.Samples.Skip(1)), p => Assert.True(p.Second.TimeRel > p.First.TimeRel));
        Assert.All(g.Samples, s => Assert.True(Math.Abs(s.Lat) <= 90 && Math.Abs(s.Lon) <= 180));

        string outDir = Path.Combine(Path.GetTempPath(), "m2i-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, Cli.Run(new[] { file, "--out-dir", outDir, "--no-ini" }));
            string gpx = Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".gpx");
            string first = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(gpx), "<time>([^<]*)</time>").Groups[1].Value;
            Assert.Equal(g.StartUtc!.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), first);
        }
        finally { if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true); }
    }
}
