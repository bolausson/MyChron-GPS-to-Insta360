using System.Globalization;
using System.Text;

namespace MyChron2Insta360.Core;

/// <summary>A single logged channel (column) in an AiM CSV export.</summary>
public sealed class Channel
{
    public required string Name { get; init; }
    public string Unit { get; init; } = "";
    public int Index { get; init; }
}

/// <summary>Parsed representation of an AiM RaceStudio3 "AiM CSV File" export.</summary>
public sealed class AimCsvData
{
    /// <summary>Metadata key -> its value(s). First value is the scalar; arrays hold e.g. Beacon Markers.</summary>
    public Dictionary<string, string[]> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Channel> Channels { get; } = new();
    /// <summary>One row per sample; index aligns with <see cref="Channels"/>. NaN = missing/unparseable.</summary>
    public List<double[]> Rows { get; } = new();

    public string? GetMeta(string key) =>
        Metadata.TryGetValue(key, out var v) && v.Length > 0 && !string.IsNullOrWhiteSpace(v[0]) ? v[0] : null;

    public double SampleRateHz =>
        double.TryParse(GetMeta("Sample Rate"), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Resolve a channel column index by name (case-insensitive). -1 if absent.</summary>
    public int IndexOf(string channelName)
    {
        for (int i = 0; i < Channels.Count; i++)
            if (string.Equals(Channels[i].Name, channelName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}

/// <summary>
/// Parses AiM RaceStudio3 CSV exports. Layout: a metadata block, then a channel-name row and a
/// units row, then the data rows — sections separated by blank lines. Channels are addressed by
/// NAME, never by fixed column index, because the channel set/order varies by logger and config.
/// </summary>
public static class AimCsvParser
{
    public static AimCsvData Parse(string path)
    {
        using var reader = new StreamReader(path);
        return Parse(reader);
    }

    public static AimCsvData Parse(TextReader reader)
    {
        var data = new AimCsvData();

        // Group non-empty lines into paragraphs separated by blank lines.
        var paragraphs = new List<List<string>>();
        var current = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0) { paragraphs.Add(current); current = new List<string>(); }
            }
            else current.Add(line);
        }
        if (current.Count > 0) paragraphs.Add(current);

        if (paragraphs.Count < 2)
            throw new FormatException("Unexpected AiM CSV structure: expected a metadata block followed by a header/data block.");

        // Paragraph 0 = metadata (key,value[,value...]).
        foreach (var row in paragraphs[0])
        {
            var f = SplitCsv(row);
            if (f.Length == 0 || string.IsNullOrWhiteSpace(f[0])) continue;
            data.Metadata[f[0]] = f.Skip(1).ToArray();
        }

        // Paragraph 1 = channel names (row 0), units (row 1). Some exports put data in the same
        // paragraph (no blank line before data), so any extra rows here are treated as data too.
        var header = paragraphs[1];
        var names = SplitCsv(header[0]);
        var units = header.Count > 1 ? SplitCsv(header[1]) : Array.Empty<string>();
        for (int i = 0; i < names.Length; i++)
            data.Channels.Add(new Channel { Name = names[i].Trim(), Unit = i < units.Length ? units[i].Trim() : "", Index = i });

        // Data = leftover rows of paragraph 1 (index >= 2) plus everything from paragraph 2 onward.
        var dataRows = header.Skip(2).Concat(paragraphs.Skip(2).SelectMany(p => p));
        int cols = data.Channels.Count;
        foreach (var row in dataRows)
        {
            var f = SplitCsv(row);
            var vals = new double[cols];
            for (int i = 0; i < cols; i++)
                vals[i] = i < f.Length && double.TryParse(f[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                    ? d : double.NaN;
            data.Rows.Add(vals);
        }

        return data;
    }

    /// <summary>Split one CSV line, honouring double-quoted fields and "" escaped quotes.</summary>
    public static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }
}
