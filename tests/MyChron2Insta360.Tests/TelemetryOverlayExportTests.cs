using MyChron2Insta360.App;
using MyChron2Insta360.Core;

namespace MyChron2Insta360.Tests;

/// <summary>
/// Covers the Telemetry Overlay export end to end, through the real CLI, so the tilt correction wiring
/// is part of what is tested rather than assumed.
/// </summary>
public class TelemetryOverlayExportTests
{
    private static void Convert(SyntheticExport export, params string[] extraArgs)
    {
        var args = new List<string> { export.Folder, "--format", "telemetry-overlay", "--out-dir", export.OutDir };
        args.AddRange(extraArgs);
        Assert.Equal(0, Cli.Run(args.ToArray()));
        Assert.True(File.Exists(export.ExpectedCsv), "no Telemetry Overlay CSV was written");
    }

    // ---- (a) the tilt correction must reach this export, and must not quietly stop reaching it ----

    [Fact]
    public void Tilt_correction_reaches_the_csv()
    {
        using var export = new SyntheticExport(tiltDeg: 13.0);
        Convert(export, "--tilt");
        var csv = new AimCsv(export.ExpectedCsv);

        // Levelled: gravity is back on the vertical axis, so nothing bleeds into the inline channel.
        Assert.InRange(csv.Mean("InlineAcc"), -0.01, 0.01);
        Assert.InRange(csv.Mean("VerticalAcc"), 0.99, 1.01);
    }

    [Fact]
    public void Without_tilt_the_inline_channel_keeps_its_offset()
    {
        using var export = new SyntheticExport(tiltDeg: 13.0);
        Convert(export);
        var csv = new AimCsv(export.ExpectedCsv);

        // InlineAcc = -AccelerometerX, and a 13 degree mount leaves sin(13) = 0.225 g of gravity there.
        double expected = -Math.Sin(13.0 * Math.PI / 180.0);
        Assert.InRange(csv.Mean("InlineAcc"), expected - 0.01, expected + 0.01);
        Assert.InRange(csv.Mean("VerticalAcc"), Math.Cos(13.0 * Math.PI / 180.0) - 0.01, 1.0);
    }

    [Fact]
    public void Tilt_changes_the_file_so_a_regression_cannot_hide()
    {
        using var levelled = new SyntheticExport(tiltDeg: 13.0);
        using var asRecorded = new SyntheticExport(tiltDeg: 13.0);
        Convert(levelled, "--tilt");
        Convert(asRecorded);

        double corrected = new AimCsv(levelled.ExpectedCsv).Mean("InlineAcc");
        double raw = new AimCsv(asRecorded.ExpectedCsv).Mean("InlineAcc");
        Assert.True(Math.Abs(raw - corrected) > 0.2,
            $"tilt made no difference to the export (raw {raw:0.000} vs corrected {corrected:0.000})");
    }

    [Fact]
    public void Manual_tilt_angle_is_applied()
    {
        using var export = new SyntheticExport(tiltDeg: 13.0);
        Convert(export, "--tilt", "13");
        Assert.InRange(new AimCsv(export.ExpectedCsv).Mean("InlineAcc"), -0.01, 0.01);
    }

    // ---- (b) an unknown channel must still produce a loadable file, carrying that channel ----

    [Fact]
    public void Unknown_channel_is_kept_as_an_extra_stream()
    {
        using var export = new SyntheticExport(13.0, 60,
            ("EGT", "C", i => 600 + i % 50),                 // no alias exists for EGT at all
            ("Lambda", "lambda", i => 0.9 + (i % 10) / 100.0));
        Convert(export, "--tilt");
        var csv = new AimCsv(export.ExpectedCsv);

        // Present, verbatim, with its own units: Telemetry Overlay picks these up as extra streams.
        Assert.True(csv.Has("EGT"), $"EGT was dropped; file has: {string.Join(", ", csv.Headers)}");
        Assert.Equal("C", csv.UnitOf("EGT"));
        Assert.True(csv.Has("Lambda"));
        Assert.Equal(600, csv.Column("EGT")[0], 3);

        // And the file is still structurally loadable with the known channels still bound.
        Assert.True(csv.Has("InlineAcc"));
        Assert.Equal("g", csv.UnitOf("InlineAcc"));
        Assert.True(csv.Rows.Count > 1000);

        // Re-declaring known headers is what makes the whole file fail to load, so none is written.
        Assert.False(File.Exists(Path.Combine(export.OutDir, "remapheaders.txt")));
        Assert.False(File.Exists(Path.Combine(export.Folder, "remapheaders.txt")));
    }

    [Fact]
    public void Documented_renames_bind_and_all_zero_channels_are_dropped()
    {
        using var export = new SyntheticExport(13.0, 60,
            ("Internal Batt", "V", _ => 4.1),        // "Internal Batt (V)" is not a name TO knows
            ("Logger Temperature", "C", _ => 26.4),  // needs the degree sign to bind
            ("Water Temp", "C", i => 80 + i % 5),
            ("RPM", "rpm", _ => 0));                 // nothing connected: should be dropped
        Convert(export);
        var csv = new AimCsv(export.ExpectedCsv);

        Assert.True(csv.Has("Battery"));
        Assert.Equal("V", csv.UnitOf("Battery"));
        Assert.True(csv.Has("Logger Temperature"));
        Assert.Equal("°C", csv.UnitOf("Logger Temperature"));   // plain "C" would silently not bind
        Assert.True(csv.Has("Coolant Temperature"));
        Assert.Equal("C", csv.UnitOf("Coolant Temperature"));   // and here the degree sign would not
        Assert.False(csv.Has("RPM"));
        Assert.False(csv.Has("Internal Batt"));
    }

    // ---- format and structure ----

    [Fact]
    public void File_matches_the_shape_telemetry_overlay_looks_for()
    {
        using var export = new SyntheticExport();
        Convert(export, "--tilt");
        string text = File.ReadAllText(export.ExpectedCsv);
        var csv = new AimCsv(export.ExpectedCsv);

        Assert.StartsWith("\"Format\",\"AiM CSV File\"", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));           // CRLF only
        Assert.Equal(14, csv.HeaderLine);                                // 13 preamble lines, then a blank
        Assert.Equal("Time", csv.Headers[0]);
        Assert.Equal("s", csv.Units[0]);
        Assert.Equal(csv.HeaderLine + 3, csv.FirstDataLine);             // units row, blank, then data
        Assert.All(csv.Lines.Skip(csv.FirstDataLine).Where(l => l.Length > 0),
                   l => Assert.True(l.StartsWith('"') && l.EndsWith('"'), "every field must be quoted"));
        Assert.Equal("50", csv.Preamble("Sample Rate"));
        Assert.Equal(3, csv.BeaconMarkers().Length);                     // one per lap
        Assert.Equal(new[] { 0.0, 1.0, 2.0 }, csv.Column("Lap").Distinct().OrderBy(v => v));
    }

    [Fact]
    public void Gps_speed_is_converted_to_kmh()
    {
        using var export = new SyntheticExport();
        Convert(export);
        var csv = new AimCsv(export.ExpectedCsv);

        Assert.Equal("km/h", csv.UnitOf("GPS Speed"));
        // The source is m/s and starts at 20, so the first row must read 72 km/h.
        Assert.Equal(72.0, csv.Column("GPS Speed")[0], 1);
    }

    [Fact]
    public void Steering_is_negated_by_default_and_can_be_left_alone()
    {
        using var negated = new SyntheticExport(13.0, 60, ("Steering Angle", "deg", _ => 10.0));
        using var asLogged = new SyntheticExport(13.0, 60, ("Steering Angle", "deg", _ => 10.0));
        Convert(negated);
        Convert(asLogged, "--no-to-invert-steering");

        Assert.Equal(-10.0, new AimCsv(negated.ExpectedCsv).Column("Steering Angle")[0], 3);
        Assert.Equal(10.0, new AimCsv(asLogged.ExpectedCsv).Column("Steering Angle")[0], 3);
    }

    [Fact]
    public void Lateral_is_left_alone_by_default_and_can_be_inverted()
    {
        using var normal = new SyntheticExport(tiltDeg: 0);
        using var flipped = new SyntheticExport(tiltDeg: 0);
        Convert(normal);
        Convert(flipped, "--to-invert-lateral");

        double a = new AimCsv(normal.ExpectedCsv).Mean("LateralAcc");
        double b = new AimCsv(flipped.ExpectedCsv).Mean("LateralAcc");
        Assert.Equal(a, -b, 6);
    }

    [Fact]
    public void Formats_compose()
    {
        using var export = new SyntheticExport();
        Assert.Equal(0, Cli.Run(new[] { export.Folder, "--format", "gpx,fit,telemetry-overlay",
                                        "--out-dir", export.OutDir, "--no-imu-csv" }));
        Assert.True(File.Exists(export.ExpectedCsv));
        Assert.True(File.Exists(Path.Combine(export.OutDir, "Synthetic_Session_CSV.gpx")));
        Assert.True(File.Exists(Path.Combine(export.OutDir, "Synthetic_Session_CSV.fit")));
    }
}

/// <summary>The alias table is the thing that decides whether a column binds to a gauge.</summary>
public class TelemetryOverlayAliasTests
{
    [Fact]
    public void Alias_table_is_embedded_and_complete()
    {
        Assert.Equal(483, TelemetryOverlayAliases.Count);
    }

    [Theory]
    [InlineData("Logger Temperature", "°C")]   // degree sign required here
    [InlineData("Coolant Temperature", "C")]   // and must not be present here
    [InlineData("InlineAcc", "g")]
    [InlineData("YawRate", "deg/s")]
    [InlineData("Battery", "V")]
    public void Known_names_are_recognised(string header, string units) =>
        Assert.True(TelemetryOverlayAliases.Contains(header, units));

    [Theory]
    [InlineData("Internal Batt", "V")]         // the trap: looks right, is not in the table
    [InlineData("Logger Temperature", "C")]
    [InlineData("LongAcc", "g")]
    [InlineData("Lap", "#")]
    [InlineData("EGT", "C")]
    public void Names_that_do_not_bind(string header, string units) =>
        Assert.False(TelemetryOverlayAliases.Contains(header, units));

    [Fact]
    public void Resolve_renames_only_onto_names_that_exist()
    {
        Assert.Equal(new ChannelHeader("Battery", "V", ChannelBinding.Renamed),
                     TelemetryOverlayAliases.Resolve("Internal Batt", "V"));
        Assert.Equal(new ChannelHeader("Logger Temperature", "°C", ChannelBinding.Renamed),
                     TelemetryOverlayAliases.Resolve("Logger Temperature", "C"));
        Assert.Equal(new ChannelHeader("Steering Angle", "deg", ChannelBinding.Alias),
                     TelemetryOverlayAliases.Resolve("Steering Angle", "deg"));
        Assert.Equal(new ChannelHeader("EGT", "C", ChannelBinding.Extra),
                     TelemetryOverlayAliases.Resolve("EGT", "C"));
    }
}
