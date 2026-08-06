using System.Runtime.InteropServices;
using System.Text;

namespace MyChron2Insta360.Core;

/// <summary>
/// Binding to AiM's official XRK library, which reads .xrk / .xrz logger files directly so RaceStudio
/// does not have to export CSVs first. Documented at
/// https://www.aim-sportline.com/docs/racestudio3/html/xrk-dll.html
///
/// The library is AiM's proprietary redistributable and is NOT shipped with this tool, which is GPLv3.
/// It is loaded at run time if present, and everything degrades to the CSV reader when it is not.
/// Download the SDK from the page above and put its DLLs beside the exe or in an "xrk" folder.
///
/// Two quirks of the C API are handled here rather than leaking to callers: plain and GPS-raw channel
/// times are in seconds while computed GPS channel times are in milliseconds, and the GPS sampling
/// frequency is process-global and must be set before any GPS channel is touched.
/// </summary>
internal static class XrkNative
{
    internal const string Lib = "MatLabXRK";

    /// <summary>Loaded before the main library, in dependency order.</summary>
    private static readonly string[] Dependencies = { "libz", "libiconv-2", "libxml2-2", "pthreadVC2_x64" };

    /// <summary>Known names of the main library, newest first.</summary>
    private static readonly string[] Candidates =
    {
        "MatLabXRK-2022-64-ReleaseU.dll",
        "MatLabXRK-2020-64-ReleaseU.dll",
        "MatLabXRK-2019-64-ReleaseU.dll",
        "MatLabXRK-64-ReleaseU.dll",
        "MatLabXRK.dll",
    };

    private static readonly object Gate = new();
    private static bool _tried;
    private static IntPtr _handle;
    private static string? _loadedFrom;
    private static string? _error;
    private static bool _gpsFreqSet;

    /// <summary>Extra folder to search first, from --xrk-dll or the GUI.</summary>
    public static string? SearchPath { get; set; }

    public static bool Available => Load() != IntPtr.Zero;
    public static string? LoadedFrom { get { Load(); return _loadedFrom; } }
    public static string? Error { get { Load(); return _error; } }

    /// <summary>Folders searched for the library, in order.</summary>
    public static IEnumerable<string> SearchFolders()
    {
        if (!string.IsNullOrWhiteSpace(SearchPath)) yield return SearchPath!;
        if (Environment.GetEnvironmentVariable("MYCHRON2INSTA360_XRK_DIR") is { Length: > 0 } env) yield return env;

        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        if (exeDir is { Length: > 0 })
        {
            yield return exeDir;
            yield return Path.Combine(exeDir, "xrk");
        }
        if (AppContext.BaseDirectory is { Length: > 0 } base_ && base_ != exeDir)
        {
            yield return base_;
            yield return Path.Combine(base_, "xrk");
        }
        yield return Directory.GetCurrentDirectory();
        yield return Path.Combine(Directory.GetCurrentDirectory(), "xrk");
    }

    /// <summary>Where the support DLLs might sit relative to the main library.</summary>
    private static IEnumerable<string> DependencyFolders(string libDir)
    {
        yield return libDir;
        yield return Path.Combine(libDir, "64");
        string? parent = Path.GetDirectoryName(libDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is { Length: > 0 })
        {
            yield return Path.Combine(parent, "64");
            yield return parent;
        }
    }

    private static IntPtr Load()
    {
        lock (Gate)
        {
            if (_tried) return _handle;
            _tried = true;

            if (!OperatingSystem.IsWindows())
            {
                _error = "the AiM XRK library is Windows only";
                return IntPtr.Zero;
            }

            foreach (var dir in SearchFolders().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                foreach (var name in Candidates)
                {
                    string full = Path.Combine(dir, name);
                    if (!File.Exists(full)) continue;

                    // Pull the dependencies in by absolute path first: the main library resolves them
                    // by module name, and they are not on the search path. The SDK as downloaded keeps
                    // them in a sibling "64" folder rather than beside the library, so look around.
                    foreach (var dep in Dependencies)
                        foreach (var depDir in DependencyFolders(dir))
                        {
                            string p = Path.Combine(depDir, dep + ".dll");
                            if (File.Exists(p) && NativeLibrary.TryLoad(p, out _)) break;
                        }

                    if (NativeLibrary.TryLoad(full, out var h))
                    {
                        _handle = h;
                        _loadedFrom = full;
                        NativeLibrary.SetDllImportResolver(typeof(XrkNative).Assembly,
                            (libName, asm, path) => libName == Lib ? _handle : IntPtr.Zero);
                        return _handle;
                    }
                    _error = $"found {full} but it could not be loaded (is it the 64 bit build, with its libxml2/libiconv/libz/pthread DLLs beside it?)";
                }
            }

            _error ??= "AiM's XRK library was not found. Download the SDK from " +
                       "https://www.aim-sportline.com/docs/racestudio3/html/xrk-dll.html and put its DLLs " +
                       "beside this program or in an 'xrk' folder next to it.";
            return IntPtr.Zero;
        }
    }

    /// <summary>Set once per process, before any GPS channel is read.</summary>
    public static void EnsureGpsFreq(double hz)
    {
        lock (Gate)
        {
            if (_gpsFreqSet) return;
            _gpsFreqSet = true;
            set_GPS_sample_freq(hz);
        }
    }

    public static string Str(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";

    /// <summary>The API takes byte paths, so non-ASCII folder names need explicit UTF-8.</summary>
    public static byte[] PathBytes(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        Array.Resize(ref bytes, bytes.Length + 1);
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Tm { public int Sec, Min, Hour, MDay, Mon, Year, WDay, YDay, IsDst; }

    private const CallingConvention C = CallingConvention.Cdecl;

    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_library_date();
    [DllImport(Lib, CallingConvention = C)] public static extern int open_file(byte[] fullPath);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_last_open_error();
    [DllImport(Lib, CallingConvention = C)] public static extern int close_file_i(int idxf);

    [DllImport(Lib, CallingConvention = C)] public static extern uint get_logger_id(int idxf);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_vehicle_name(int idxf);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_track_name(int idxf);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_racer_name(int idxf);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_championship_name(int idxf);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_session_type_name(int idxf);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_date_and_time(int idxf);
    [DllImport(Lib, CallingConvention = C)] public static extern int get_session_duration(int idxf, out double duration);

    [DllImport(Lib, CallingConvention = C)] public static extern int get_laps_count(int idxf);
    [DllImport(Lib, CallingConvention = C)] public static extern int get_lap_info(int idxf, int idxl, out double start, out double duration);

    [DllImport(Lib, CallingConvention = C)] public static extern int get_channels_count(int idxf);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_channel_name(int idxf, int idxc);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_channel_units(int idxf, int idxc);
    [DllImport(Lib, CallingConvention = C)] public static extern int get_channel_samples_count(int idxf, int idxc);
    [DllImport(Lib, CallingConvention = C)] public static extern int get_channel_samples(int idxf, int idxc, double[] times, double[] values, int count);

    [DllImport(Lib, CallingConvention = C)] public static extern int set_GPS_sample_freq(double hz);
    [DllImport(Lib, CallingConvention = C)] public static extern int get_GPS_channels_count(int idxf);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_GPS_channel_name(int idxf, int idxc);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_GPS_channel_units(int idxf, int idxc);
    [DllImport(Lib, CallingConvention = C)] public static extern int get_GPS_channel_samples_count(int idxf, int idxc);
    [DllImport(Lib, CallingConvention = C)] public static extern int get_GPS_channel_samples(int idxf, int idxc, double[] times, double[] values, int count);

    [DllImport(Lib, CallingConvention = C)] public static extern int get_GPS_raw_channels_count(int idxf);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_GPS_raw_channel_name(int idxf, int idxc);
    [DllImport(Lib, CallingConvention = C)] public static extern IntPtr get_GPS_raw_channel_units(int idxf, int idxc);
    [DllImport(Lib, CallingConvention = C)] public static extern int get_GPS_raw_channel_samples_count(int idxf, int idxc);
    [DllImport(Lib, CallingConvention = C)] public static extern int get_GPS_raw_channel_samples(int idxf, int idxc, double[] times, double[] values, int count);
}
