using MyChron2Insta360.App;

namespace MyChron2Insta360.Tests;

/// <summary>
/// The Sources pickers' default folder is kept in the settings file like everything else, so it has to
/// survive the trip through it, and a file saved by an older version must not lose the default.
/// </summary>
public class AppSettingsTests
{
    private static AppSettings RoundTrip(string iniText)
    {
        string dir = Path.Combine(Path.GetTempPath(), "m2i-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // An explicit path, so nothing is read from or written beside the test host.
            string ini = Path.Combine(dir, "settings.ini");
            File.WriteAllText(ini, iniText);
            var loaded = AppSettings.Load(ini, out _, out string? error);
            Assert.Null(error);
            return loaded;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Sources_pickers_start_in_RaceStudios_data_folder_by_default()
    {
        var s = new AppSettings();
        Assert.Equal(@"C:\AIM_SPORT\RaceStudio3\user\data", s.SourcesDir);
        Assert.False(s.AlwaysOpenSourcesDir);
    }

    [Fact]
    public void Sources_path_and_tickbox_survive_save_and_load()
    {
        var saved = new AppSettings { SourcesDir = @"D:\Racing\RaceStudio data", AlwaysOpenSourcesDir = true };
        var loaded = RoundTrip(saved.ToIni());
        Assert.Equal(@"D:\Racing\RaceStudio data", loaded.SourcesDir);
        Assert.True(loaded.AlwaysOpenSourcesDir);
    }

    [Fact]
    public void A_file_from_before_the_option_keeps_the_default()
    {
        var loaded = RoundTrip("[MyChron2Insta360]\r\nformat = gpx\r\nmerge = true\r\n");
        Assert.Equal(AppSettings.RaceStudioDataDir, loaded.SourcesDir);
        Assert.False(loaded.AlwaysOpenSourcesDir);
        Assert.True(loaded.Merge);
    }

    [Theory]
    [InlineData("", @"C:\AIM_SPORT\RaceStudio3\user\data")]
    [InlineData("   ", @"C:\AIM_SPORT\RaceStudio3\user\data")]
    [InlineData(@"""D:\Racing""", @"D:\Racing")]
    [InlineData(@"  D:\Racing  ", @"D:\Racing")]
    public void An_empty_box_means_RaceStudios_folder_and_quotes_are_dropped(string typed, string expected) =>
        Assert.Equal(expected, AppSettings.SourcesDirOrDefault(typed));
}
