using MyChron2Insta360.App;

namespace MyChron2Insta360.Tests;

/// <summary>
/// The shell refuses folder spellings that .NET happily accepts, and a refused default folder is dropped
/// without a word, tick box or not. So whatever passes as usable must come out in the plain spelling.
/// </summary>
public class SourcePickerTests
{
    [Fact]
    public void Odd_spellings_of_an_existing_folder_come_out_plain()
    {
        string root = Path.Combine(Path.GetTempPath(), "m2i-tests", Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        try
        {
            Assert.Equal(data, SourcePicker.UsableFolder(data));
            Assert.Equal(data, SourcePicker.UsableFolder(data.Replace('\\', '/')));
            Assert.Equal(data, SourcePicker.UsableFolder(Path.Combine(data, "..", "data")));
            Assert.Equal(data, SourcePicker.UsableFolder(data.Replace(@"\data", @"\\data")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Anything_but_a_full_path_to_an_existing_folder_is_not_usable()
    {
        string root = Path.Combine(Path.GetTempPath(), "m2i-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "session.xrk");
        File.WriteAllText(file, "");
        try
        {
            Assert.Null(SourcePicker.UsableFolder(Path.Combine(root, "not-there")));
            Assert.Null(SourcePicker.UsableFolder(file));
            Assert.Null(SourcePicker.UsableFolder("m2i-tests"));
            Assert.Null(SourcePicker.UsableFolder(""));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
