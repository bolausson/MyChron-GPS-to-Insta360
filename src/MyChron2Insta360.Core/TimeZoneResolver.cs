using GeoTimeZone;

namespace MyChron2Insta360.Core;

/// <summary>Maps a GPS coordinate to an IANA timezone (offline, via GeoTimeZone) then to a TimeZoneInfo.</summary>
public static class TimeZoneResolver
{
    /// <summary>IANA zone id for a coordinate, e.g. "Europe/Paris". Null if it can't be determined.</summary>
    public static string? IanaIdFor(double latitude, double longitude)
    {
        var result = TimeZoneLookup.GetTimeZone(latitude, longitude);
        return string.IsNullOrWhiteSpace(result?.Result) ? null : result!.Result;
    }

    /// <summary>Resolve a coordinate straight to a TimeZoneInfo (DST-aware). Null if lookup fails.</summary>
    public static TimeZoneInfo? FromCoordinates(double latitude, double longitude)
    {
        var iana = IanaIdFor(latitude, longitude);
        if (iana is null) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch { return null; }
    }
}
