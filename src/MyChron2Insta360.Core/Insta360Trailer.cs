using System.Buffers.Binary;
using System.Text;

namespace MyChron2Insta360.Core;

/// <summary>GPS + IMU pulled from an Insta360 file's metadata trailer.</summary>
public sealed class Insta360Telemetry
{
    public List<TrackPoint> Gps { get; } = new();
    public List<ImuSample> Imu { get; } = new();
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public bool HasGyro { get; set; }
    /// <summary>Diagnostic: every trailer record found, as (id, payload size).</summary>
    public List<(int Id, long Size)> Records { get; } = new();
    /// <summary>Diagnostic: first bytes of the IMU (id 3) record, for format decoding.</summary>
    public byte[] ImuHead { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Native reader for the proprietary trailer appended to Insta360 .insv/.lrv/.insp files (ISO-BMFF
/// MP4 plus an EOF trailer with magic "8db42d694ccc418790edff439fe026bf"). The trailer is read from
/// EOF backwards: a 72-byte footer holds the trailer size, then a chain of records whose 6-byte
/// descriptor (format u8, id u8, size u32-LE) sits AFTER its payload. We extract the GPS record
/// (id 7, 53-byte samples, absolute UTC) and the IMU record (id 3, accel g + gyro). No dependencies.
/// Format from telemetry-parser (MIT/Apache) and ExifTool, reimplemented clean-room.
/// </summary>
public static class Insta360Trailer
{
    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("8db42d694ccc418790edff439fe026bf");
    private const int FooterSize = 72;   // 32 reserved + u32 size + u32 version + 32 magic
    private const int DescriptorSize = 6; // format u8 + id u8 + size u32-LE
    private const int GpsRecordSize = 53;
    private const double RadToDeg = 180.0 / Math.PI;
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>True if the file ends with the Insta360 trailer magic.</summary>
    public static bool HasTrailer(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            if (fs.Length < FooterSize) return false;
            fs.Seek(-32, SeekOrigin.End);
            Span<byte> tail = stackalloc byte[32];
            fs.ReadExactly(tail);
            return tail.SequenceEqual(MagicBytes);
        }
        catch { return false; }
    }

    public static Insta360Telemetry Parse(string path)
    {
        var result = new Insta360Telemetry();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        long len = fs.Length;
        if (len < FooterSize) throw new InvalidDataException("File too small to contain an Insta360 trailer.");

        var footer = new byte[FooterSize];
        fs.Seek(len - FooterSize, SeekOrigin.Begin);
        fs.ReadExactly(footer);
        if (!footer.AsSpan(40, 32).SequenceEqual(MagicBytes))
            throw new InvalidDataException("Not an Insta360 file (trailer magic not found at end of file).");

        uint trailerSize = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(32, 4));
        long trailerStart = len - trailerSize;
        if (trailerStart < 0 || trailerSize < FooterSize)
            throw new InvalidDataException("Insta360 trailer size is invalid.");

        // The GPS + IMU records are small; the bulk of the trailer is thumbnails. Read the whole
        // trailer blob (tens of MB) and index it in memory.
        var buf = new byte[trailerSize];
        fs.Seek(trailerStart, SeekOrigin.Begin);
        fs.ReadExactly(buf);

        // Newer models (X3/X4/X5, Ace Pro) write a directory table (id 0) as the last record: an array
        // of 10-byte entries (id u8, format u8, size u32-LE, offset u32-LE from trailer start). Older
        // files have no directory, so fall back to walking the record chain backwards from the footer.
        int lastDesc = buf.Length - FooterSize - DescriptorSize;
        bool viaDirectory = false;
        if (lastDesc >= 0)
        {
            byte id0 = buf[lastDesc + 1];
            uint size0 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(lastDesc + 2, 4));
            long dirStart = lastDesc - (long)size0;
            if (id0 == 0 && dirStart >= 0)
            {
                var dir = buf.AsSpan((int)dirStart, (int)size0);
                for (int e = 0; e + 10 <= dir.Length; e += 10)
                {
                    int recId = dir[e];
                    uint recSize = BinaryPrimitives.ReadUInt32LittleEndian(dir.Slice(e + 2, 4));
                    uint recOffset = BinaryPrimitives.ReadUInt32LittleEndian(dir.Slice(e + 6, 4));
                    if (recOffset + recSize > (uint)buf.Length) continue;
                    Dispatch(recId, buf.AsSpan((int)recOffset, (int)recSize), result);
                }
                viaDirectory = true;
            }
        }
        if (!viaDirectory)
        {
            int pos = buf.Length - FooterSize - DescriptorSize;
            while (pos >= 0)
            {
                byte id = buf[pos + 1];
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos + 2, 4));
                long payloadStart = pos - (long)size;
                if (size == 0 || payloadStart < 0) break;
                Dispatch(id, buf.AsSpan((int)payloadStart, (int)size), result);
                pos = (int)payloadStart - DescriptorSize;
            }
        }

        result.Gps.Sort((a, b) => a.Utc.CompareTo(b.Utc));
        if (result.Imu.Count > 0)
        {
            result.Imu.Sort((a, b) => a.ElapsedS.CompareTo(b.ElapsedS));
            double t0 = result.Imu[0].ElapsedS;
            foreach (var s in result.Imu) s.ElapsedS -= t0; // re-base to 0
        }
        return result;
    }

    private static void Dispatch(int id, ReadOnlySpan<byte> payload, Insta360Telemetry r)
    {
        r.Records.Add((id, payload.Length));
        switch (id)
        {
            case 7: ParseGps(payload, r); break;      // GPS
            case 3: ParseImu(payload, r); break;      // accel + gyro
            case 1: ParseMetadata(payload, r); break; // protobuf: model, serial
        }
    }

    private static void ParseGps(ReadOnlySpan<byte> p, Insta360Telemetry r)
    {
        for (int o = 0; o + GpsRecordSize <= p.Length; o += GpsRecordSize)
        {
            ulong sec = BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(o, 8));
            ushort ms = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(o + 8, 2));
            char fix = (char)p[o + 10];
            if (fix == 'V') continue; // void fix, no valid position

            double lat = BinaryPrimitives.ReadDoubleLittleEndian(p.Slice(o + 11, 8));
            char latDir = (char)p[o + 19];
            double lon = BinaryPrimitives.ReadDoubleLittleEndian(p.Slice(o + 20, 8));
            char lonDir = (char)p[o + 28];
            double speed = BinaryPrimitives.ReadDoubleLittleEndian(p.Slice(o + 29, 8)); // m/s
            double track = BinaryPrimitives.ReadDoubleLittleEndian(p.Slice(o + 37, 8)); // deg
            double alt = BinaryPrimitives.ReadDoubleLittleEndian(p.Slice(o + 45, 8));   // m

            if (latDir == 'S') lat = -lat;
            if (lonDir is 'W' or 'O') lon = -lon; // 'O' = French "Ouest" on some units
            if (double.IsNaN(lat) || double.IsNaN(lon) || (lat == 0 && lon == 0)) continue;
            if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180) continue;

            r.Gps.Add(new TrackPoint
            {
                Utc = UnixEpoch.AddSeconds(sec).AddMilliseconds(ms),
                Lat = lat,
                Lon = lon,
                EleM = double.IsNaN(alt) ? null : alt,
                SpeedMs = double.IsNaN(speed) ? null : speed,
                HeadingDeg = double.IsNaN(track) ? null : Normalize360(track),
            });
        }
    }

    private const int ImuSampleSize = 20;

    private static void ParseImu(ReadOnlySpan<byte> p, Insta360Telemetry r)
    {
        // X-series IMU = 20-byte samples (~1000 Hz): u64 timestamp (microseconds) + 6x u16 centered at
        // 32768 and scaled /1000: accel xyz (g), gyro xyz (rad/s). Verified byte-for-byte vs ExifTool.
        if (p.Length < ImuSampleSize) return;
        for (int o = 0; o + ImuSampleSize <= p.Length; o += ImuSampleSize)
        {
            ulong tsUs = BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(o, 8));
            r.Imu.Add(new ImuSample
            {
                ElapsedS = tsUs / 1_000_000.0,
                Ax = Centered(p, o + 8),
                Ay = Centered(p, o + 10),
                Az = Centered(p, o + 12),
                Gx = Centered(p, o + 14) * RadToDeg, // rad/s -> deg/s
                Gy = Centered(p, o + 16) * RadToDeg,
                Gz = Centered(p, o + 18) * RadToDeg,
            });
        }
        r.HasGyro = true;
    }

    private static double Centered(ReadOnlySpan<byte> p, int offset) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(offset, 2)) - 32768) / 1000.0;

    private static void ParseMetadata(ReadOnlySpan<byte> p, Insta360Telemetry r)
    {
        // Minimal protobuf scan: field 1 = serial (string), field 2 = camera_type (string).
        int i = 0;
        while (i < p.Length)
        {
            ulong tag = ReadVarint(p, ref i);
            if (i > p.Length) break;
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 0x7);
            switch (wire)
            {
                case 0: ReadVarint(p, ref i); break;            // varint
                case 1: i += 8; break;                          // 64-bit
                case 5: i += 4; break;                          // 32-bit
                case 2:                                         // length-delimited
                    ulong lenL = ReadVarint(p, ref i);
                    int l = (int)lenL;
                    if (i + l > p.Length) return;
                    if (field == 1) r.Serial ??= Encoding.UTF8.GetString(p.Slice(i, l));
                    else if (field == 2) r.Model ??= Encoding.UTF8.GetString(p.Slice(i, l));
                    i += l;
                    break;
                default: return; // unknown wire type, stop
            }
        }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> p, ref int i)
    {
        ulong v = 0; int shift = 0;
        while (i < p.Length)
        {
            byte b = p[i++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
            if (shift >= 64) break;
        }
        return v;
    }

    private static double Normalize360(double deg)
    {
        double d = deg % 360.0;
        return d < 0 ? d + 360.0 : d;
    }
}
