using System.Text.RegularExpressions;

namespace MyChron2Insta360.Tests;

/// <summary>
/// Reads back an AiM RS2Analysis file the way Telemetry Overlay finds its way around one: locate the
/// header row with ^"Time" followed by ^"s(ec)?", then the first data row with ^"?\d. Parsing it this
/// way means a test failure here is also a real load failure there.
/// </summary>
internal sealed class AimCsv
{
    public List<string> Lines { get; } = new();
    public List<string> Headers { get; } = new();
    public List<string> Units { get; } = new();
    public List<string[]> Rows { get; } = new();
    public int HeaderLine { get; }
    public int FirstDataLine { get; }

    public AimCsv(string path)
    {
        // newline: none, so the CRLF the format requires survives to be asserted on.
        Lines.AddRange(File.ReadAllText(path).Split("\r\n"));

        HeaderLine = -1;
        for (int i = 0; i + 1 < Lines.Count; i++)
        {
            if (!Regex.IsMatch(Lines[i], "^\"Time\"")) continue;
            if (!Regex.IsMatch(Lines[i + 1], "^\"s(ec)?\"")) continue;
            HeaderLine = i;
            break;
        }
        if (HeaderLine < 0) throw new InvalidOperationException("no header row: Telemetry Overlay would not load this");

        Headers.AddRange(Split(Lines[HeaderLine]));
        Units.AddRange(Split(Lines[HeaderLine + 1]));

        FirstDataLine = -1;
        for (int i = HeaderLine + 2; i < Lines.Count; i++)
        {
            if (!Regex.IsMatch(Lines[i], "^\"?\\d")) continue;
            FirstDataLine = i;
            break;
        }
        if (FirstDataLine < 0) throw new InvalidOperationException("no data rows");

        for (int i = FirstDataLine; i < Lines.Count; i++)
        {
            if (Lines[i].Length == 0) continue;
            Rows.Add(Split(Lines[i]).ToArray());
        }
    }

    public string Preamble(string key) =>
        Lines.FirstOrDefault(l => l.StartsWith($"\"{key}\",")) is string line
            ? string.Join(",", Split(line).Skip(1))
            : "";

    public string[] BeaconMarkers() =>
        Lines.FirstOrDefault(l => l.StartsWith("\"Beacon Markers\"")) is string line
            ? Split(line).Skip(1).ToArray()
            : Array.Empty<string>();

    public bool Has(string header) => Headers.Contains(header);

    public string UnitOf(string header) => Units[Headers.IndexOf(header)];

    public double[] Column(string header)
    {
        int i = Headers.IndexOf(header);
        Assert.True(i >= 0, $"column '{header}' is missing; file has: {string.Join(", ", Headers)}");
        return Rows.Select(r => double.Parse(r[i], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    public double Mean(string header) => Column(header).Average();

    private static IEnumerable<string> Split(string line) =>
        line.Split(',').Select(c => c.Trim().Trim('"'));
}
