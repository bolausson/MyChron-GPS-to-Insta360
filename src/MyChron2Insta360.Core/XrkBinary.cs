using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MyChron2Insta360.Core;

/// <summary>
/// Reads AiM .xrk / .xrz logger files directly, with no proprietary library involved.
///
/// The format is a stream of self-describing messages. Header messages are
/// "&lt;h" + token + length + version + "&gt;" + payload + "&lt;" + token + checksum + "&gt;", and carry the
/// channel definitions (CHS), GPS fixes, lap markers and session strings. Sample data arrives in "(S"
/// (one sample), "(G" (one sample for each channel of a group), "(M" (a burst) and "(c" (expansion
/// device) messages. Anything unrecognised is skipped a byte at a time until the stream re-syncs, which
/// is how the reference implementation copes with vendor extensions.
///
/// Format knowledge comes from libxrk (https://github.com/m3rlin45/libxrk), MIT licensed, which builds
/// on TrackDataAnalysis by Scott Smith. See THIRD-PARTY-NOTICES.md. Nothing is copied or derived from
/// AiM's own library; this is an independent implementation of the same file format, checked against a
/// real session where it reproduces AiM's library exactly (identical sample times, values equal to
/// float32 print precision) and matches the RaceStudio CSV export to that export's rounding.
/// </summary>
public static class XrkBinary
{
    // Sample decoders, keyed by the channel's decoder_type byte.
    private enum Dec { I32, U16, I16, F32, U8, F16 }

    private static Dec Decoder(int type) => type switch
    {
        1 or 15 => Dec.U16,
        4 or 11 => Dec.I16,
        6 => Dec.F32,
        13 => Dec.U8,
        20 => Dec.F16,
        _ => Dec.I32,
    };

    /// <summary>Unit byte (low 7 bits) to unit string. The high bit marks a calibrated channel.</summary>
    private static readonly Dictionary<int, string> Units = new()
    {
        [1] = "%", [3] = "g", [4] = "deg", [5] = "deg/s", [6] = "", [9] = "Hz", [11] = "", [12] = "mm",
        [14] = "bar", [15] = "rpm", [16] = "km/h", [17] = "C", [18] = "ms", [19] = "Nm", [20] = "km/h",
        [21] = "mV", [22] = "l", [24] = "l/s", [26] = "time?", [27] = "A", [30] = "lambda",
        [31] = "gear", [33] = "%", [43] = "kg",
    };

    private sealed class Channel
    {
        public int Index;
        public string Name = "", ShortName = "", Unit = "";
        public int DecoderType, SampleSize, PeriodMs, SourceType;
        public double Scale = 1.0;
        public List<double> Times { get; } = new();
        public List<double> Values { get; } = new();
    }

    private sealed class State
    {
        public Dictionary<int, Channel> Channels = new();
        public Dictionary<int, int[]> Groups = new();
        public List<byte[]> Gps = new();
        public List<(int Number, long DurationMs, long EndMs)> Laps = new();
        public Dictionary<string, string> Strings = new(StringComparer.Ordinal);
        public long SkippedBytes;
    }

    public static XrkData Load(string path)
    {
        byte[] bytes = Decompress(File.ReadAllBytes(path));
        var state = new State();
        Walk(bytes, state, 0);

        var data = new XrkData();
        data.Gps.Name = Path.GetFileNameWithoutExtension(path);
        data.Gps.SourceFile = Path.GetFullPath(path);

        // Times are logger timecodes in milliseconds; everything is reported from the session start,
        // which is the earliest timecode any channel or GPS fix carries.
        long origin = long.MaxValue;
        foreach (var c in state.Channels.Values)
            if (c.Times.Count > 0) origin = Math.Min(origin, (long)c.Times[0]);
        foreach (var g in state.Gps)
            if (g.Length >= 4) origin = Math.Min(origin, BinaryPrimitives.ReadInt32LittleEndian(g));
        if (origin == long.MaxValue) origin = 0;

        BuildChannels(state, data, origin);
        BuildGps(state, data, origin);
        BuildLaps(state, data, origin);
        ReadInfo(state, data);
        data.Info.UnreadBytes = state.SkippedBytes;
        data.Info.TotalBytes = bytes.Length;
        return data;
    }

    /// <summary>An .xrz is just a zlib-compressed .xrk.</summary>
    private static byte[] Decompress(byte[] raw)
    {
        if (raw.Length < 2 || raw[0] != 0x78) return raw;
        try
        {
            using var src = new MemoryStream(raw);
            using var z = new ZLibStream(src, CompressionMode.Decompress);
            using var outp = new MemoryStream(raw.Length * 4);
            z.CopyTo(outp);
            return outp.ToArray();
        }
        catch (InvalidDataException) { return raw; }
    }

    private static void Walk(ReadOnlySpan<byte> d, State s, int depth)
    {
        int i = 0;
        while (i < d.Length - 1)
        {
            // Header message: '<h' token(4) length(4) version(1) '>' payload '<' token(4) sum(2) '>'
            if (d[i] == 0x3C && d[i + 1] == 0x68 && i + 12 <= d.Length)
            {
                uint token = BinaryPrimitives.ReadUInt32LittleEndian(d[(i + 2)..]);
                int len = BinaryPrimitives.ReadInt32LittleEndian(d[(i + 6)..]);
                int end = i + 12 + len + 8;
                if (len >= 0 && end <= d.Length && d[i + 11] == 0x3E)
                {
                    var payload = d.Slice(i + 12, len);
                    OnHeader(TokenName(token), payload, s, depth);
                    i = end;
                    continue;
                }
            }
            if (d[i] == 0x28 && i + 8 <= d.Length)
            {
                int consumed = OnData(d[i..], s);
                if (consumed > 0) { i += consumed; continue; }
            }
            s.SkippedBytes++;
            i++;
        }
    }

    /// <summary>Tokens are a little-endian uint32 of 3 or 4 ASCII characters, 3 padded with a space.</summary>
    private static string TokenName(uint token)
    {
        Span<char> c = stackalloc char[4];
        for (int k = 0; k < 4; k++) c[k] = (char)((token >> (8 * k)) & 0xFF);
        return new string(c).TrimEnd(' ', '\0');
    }

    private static void OnHeader(string token, ReadOnlySpan<byte> p, State s, int depth)
    {
        switch (token)
        {
            case "CNF":
            case "ENF":
                if (depth < 8) Walk(p, s, depth + 1);   // configuration blocks nest
                break;

            case "CHS" when p.Length >= 112:
            {
                int idx = BinaryPrimitives.ReadUInt16LittleEndian(p);
                int unitByte = p[12];
                string unit = Units.GetValueOrDefault(unitByte & 0x7F, "");
                // A calibrated millivolt channel is reported in volts, which is how the logger's own
                // battery channel comes out at 4.07 V rather than 4074.
                bool mvToV = (unitByte & 0x80) != 0 && unit == "mV";
                s.Channels[idx] = new Channel
                {
                    Index = idx,
                    SourceType = p[16],
                    DecoderType = p[20],
                    ShortName = NullTerm(p.Slice(24, 8)),
                    Name = NullTerm(p.Slice(32, 24)),
                    PeriodMs = (int)(BinaryPrimitives.ReadUInt32LittleEndian(p[64..]) / 1000),
                    SampleSize = p[72],
                    Unit = mvToV ? "V" : unit,
                    Scale = mvToV ? 0.001 : 1.0,
                };
                break;
            }

            case "GRP" when p.Length >= 4:
            {
                int gi = BinaryPrimitives.ReadUInt16LittleEndian(p);
                int n = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
                if (4 + n * 2 > p.Length) break;
                var members = new int[n];
                for (int k = 0; k < n; k++) members[k] = BinaryPrimitives.ReadUInt16LittleEndian(p[(4 + k * 2)..]);
                s.Groups[gi] = members;
                break;
            }

            case "GPS":
            case "GPS1":
                if (p.Length >= 56) s.Gps.Add(p[..56].ToArray());
                break;

            case "LAP" when p.Length >= 20:
                s.Laps.Add((s.Laps.Count + 1,
                            BinaryPrimitives.ReadUInt32LittleEndian(p[4..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(p[16..])));
                break;

            // Session strings. Keep the first non-empty one: the file repeats some of these empty.
            case "RCR": case "VEH": case "CMP": case "VTY": case "NDV": case "TRK":
            {
                string v = NullTerm(p);
                if (v.Length > 0 && !s.Strings.ContainsKey(token)) s.Strings[token] = v;
                break;
            }
        }
    }

    /// <summary>Returns the bytes consumed, or 0 if this is not a data message we can read.</summary>
    private static int OnData(ReadOnlySpan<byte> d, State s)
    {
        switch (d[1])
        {
            case 0x53:   // '(S' one sample
            {
                int idx = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]);
                if (!s.Channels.TryGetValue(idx, out var ch) || ch.SampleSize == 0) return 0;
                int total = 9 + ch.SampleSize;
                if (total > d.Length || d[total - 1] != 0x29) return 0;
                Add(ch, BinaryPrimitives.ReadInt32LittleEndian(d[2..]), d.Slice(8, ch.SampleSize));
                return total;
            }

            case 0x47:   // '(G' one sample for each channel of a group
            {
                int gi = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]);
                if (!s.Groups.TryGetValue(gi, out var members)) return 0;
                int width = 0;
                foreach (int m in members) width += s.Channels.TryGetValue(m, out var c) ? c.SampleSize : 0;
                int total = 9 + width;
                if (width == 0 || total > d.Length || d[total - 1] != 0x29) return 0;
                int tc = BinaryPrimitives.ReadInt32LittleEndian(d[2..]);
                int at = 8;
                foreach (int m in members)
                {
                    if (!s.Channels.TryGetValue(m, out var c) || c.SampleSize == 0) continue;
                    Add(c, tc, d.Slice(at, c.SampleSize));
                    at += c.SampleSize;
                }
                return total;
            }

            case 0x4D:   // '(M' a burst, one sample per period
            {
                int idx = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]);
                int count = BinaryPrimitives.ReadUInt16LittleEndian(d[8..]);
                if (!s.Channels.TryGetValue(idx, out var ch) || ch.SampleSize == 0) return 0;
                long width = (long)ch.SampleSize * count;
                long total = 11 + width;
                if (count == 0 || total > d.Length || d[(int)total - 1] != 0x29) return 0;
                int tc = BinaryPrimitives.ReadInt32LittleEndian(d[2..]);
                for (int k = 0; k < count; k++)
                    Add(ch, tc + (long)k * ch.PeriodMs, d.Slice(10 + k * ch.SampleSize, ch.SampleSize));
                return (int)total;
            }

            case 0x63:   // '(c' expansion device, the 12-byte form that carries a CHS-sized sample
            {
                if (d.Length < 13 || d[2] != 0x00 || d[5] != 0x84 || d[6] != 0x06) return 0;
                int idx = BinaryPrimitives.ReadUInt16LittleEndian(d[3..]) >> 3;
                if (!s.Channels.TryGetValue(idx, out var ch) || ch.SampleSize == 0) return 0;
                int total = 13 + ch.SampleSize;
                if (total > d.Length || d[total - 1] != 0x29) return 0;
                Add(ch, BinaryPrimitives.ReadInt32LittleEndian(d[7..]), d.Slice(12, ch.SampleSize));
                return total;
            }
        }
        return 0;
    }

    private static void Add(Channel ch, long timecodeMs, ReadOnlySpan<byte> raw)
    {
        double v = Decoder(ch.DecoderType) switch
        {
            Dec.F16 => (double)BitConverter.ToHalf(raw),
            Dec.U16 => BinaryPrimitives.ReadUInt16LittleEndian(raw),
            Dec.I16 => BinaryPrimitives.ReadInt16LittleEndian(raw),
            Dec.F32 => BinaryPrimitives.ReadSingleLittleEndian(raw),
            Dec.U8 => raw[0],
            _ => raw.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(raw) : 0,
        };
        ch.Times.Add(timecodeMs);
        ch.Values.Add(v * ch.Scale);
    }

    private static void BuildChannels(State s, XrkData data, long origin)
    {
        var accel = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
        var gyro = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);

        foreach (var ch in s.Channels.Values.OrderBy(c => c.Index))
        {
            if (ch.Values.Count == 0) continue;
            // Source type 4 is the logger's own computed set (master clock, lap and predictive times,
            // best-lap differences, alarm thresholds, CAN containers) and 5 is the GPS container, which
            // is read from the GPS messages instead. Everything else is a real logged channel, so a
            // logger with CAN or expansion probes keeps those.
            if (ch.SourceType is 4 or 5) continue;
            if (ch.Name.StartsWith("Accelerometer", StringComparison.OrdinalIgnoreCase)) { accel[ch.Name] = ch; continue; }
            if (ch.Name.StartsWith("Gyro", StringComparison.OrdinalIgnoreCase)) { gyro[ch.Name] = ch; continue; }

            var series = new ChannelSeries { Name = ch.Name, Units = ch.Unit, SourceFile = data.Gps.SourceFile };
            for (int i = 0; i < ch.Values.Count; i++)
            {
                series.Times.Add((ch.Times[i] - origin) / 1000.0);
                series.Values.Add(ch.Values[i]);
            }
            data.Channels.Add(series);
        }

        data.Imu.AddRange(ImuBuilder.Join(Triax(accel, "AccelerometerX", "AccelerometerY", "AccelerometerZ", origin),
                                          Triax(gyro, "GyroX", "GyroY", "GyroZ", origin)));
    }

    private static List<ImuBuilder.Triax> Triax(Dictionary<string, Channel> src, string x, string y, string z, long origin)
    {
        var list = new List<ImuBuilder.Triax>();
        if (!src.TryGetValue(x, out var cx) || !src.TryGetValue(y, out var cy) || !src.TryGetValue(z, out var cz))
            return list;
        int n = Math.Min(cx.Values.Count, Math.Min(cy.Values.Count, cz.Values.Count));
        for (int i = 0; i < n; i++)
            list.Add(new ImuBuilder.Triax((cx.Times[i] - origin) / 1000.0, cx.Values[i], cy.Values[i], cz.Values[i]));
        return list;
    }

    /// <summary>GPS fixes are a 4 byte logger timecode followed by a u-blox NAV-SOL record.</summary>
    private static void BuildGps(State s, XrkData data, long origin)
    {
        foreach (var g in s.Gps)
        {
            var p = (ReadOnlySpan<byte>)g;
            int tc = BinaryPrimitives.ReadInt32LittleEndian(p);
            uint itow = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
            int week = BinaryPrimitives.ReadUInt16LittleEndian(p[12..]);
            double ex = BinaryPrimitives.ReadInt32LittleEndian(p[16..]) / 100.0;   // cm
            double ey = BinaryPrimitives.ReadInt32LittleEndian(p[20..]) / 100.0;
            double ez = BinaryPrimitives.ReadInt32LittleEndian(p[24..]) / 100.0;
            double pAcc = BinaryPrimitives.ReadUInt32LittleEndian(p[28..]) / 100.0;
            double vx = BinaryPrimitives.ReadInt32LittleEndian(p[32..]) / 100.0;   // cm/s
            double vy = BinaryPrimitives.ReadInt32LittleEndian(p[36..]) / 100.0;
            double vz = BinaryPrimitives.ReadInt32LittleEndian(p[40..]) / 100.0;

            var (lat, lon, alt) = XrkReader.EcefToGeodetic(ex, ey, ez);
            if (week > 0 && data.Info.GpsWeek == 0) { data.Info.GpsWeek = week; data.Gps.GpsWeek = week; }
            data.Gps.Samples.Add(new GpsSample
            {
                TimeRel = (tc - origin) / 1000.0,
                ItowMs = itow,
                Lat = lat,
                Lon = lon,
                Alt = alt,
                SpeedMs = Math.Sqrt(vx * vx + vy * vy + vz * vz),
                AccuracyM = pAcc,
            });
        }

        if (data.Gps.Samples.Count > 1)
        {
            double span = data.Gps.Samples[^1].TimeRel - data.Gps.Samples[0].TimeRel;
            if (span > 0) data.Gps.NativeHz = (data.Gps.Samples.Count - 1) / span;
        }
    }

    private static void BuildLaps(State s, XrkData data, long origin)
    {
        foreach (var (number, durationMs, endMs) in s.Laps)
        {
            double duration = durationMs / 1000.0;
            double start = (endMs - origin) / 1000.0 - duration;
            data.Laps.Add((number, Math.Max(0, start), duration));
        }
    }

    private static void ReadInfo(State s, XrkData data)
    {
        data.Info.Racer = s.Strings.GetValueOrDefault("RCR", "");
        data.Info.Vehicle = s.Strings.GetValueOrDefault("VEH", "");
        data.Info.Championship = s.Strings.GetValueOrDefault("CMP", "");
        data.Info.Track = s.Strings.GetValueOrDefault("TRK", "");
        data.Info.SessionType = s.Strings.GetValueOrDefault("VTY", "");
        if (data.Gps.Samples.Count > 0 && data.Info.GpsWeek > 0)
            data.Info.StartLocal = ItowTime.FromWeek(data.Info.GpsWeek, data.Gps.Samples[0].ItowMs).ToLocalTime();
        if (data.Imu.Count > 0) data.Info.DurationS = data.Imu[^1].ElapsedS;
    }

    private static string NullTerm(ReadOnlySpan<byte> b)
    {
        int z = b.IndexOf((byte)0);
        if (z >= 0) b = b[..z];
        return Encoding.ASCII.GetString(b).Trim();
    }
}
