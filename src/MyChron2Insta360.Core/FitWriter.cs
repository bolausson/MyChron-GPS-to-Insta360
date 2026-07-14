using Dynastream.Fit;

namespace MyChron2Insta360.Core;

/// <summary>
/// Writes a minimal but valid Garmin FIT Activity file (file_id -> records -> lap -> session ->
/// activity). Records from all session segments are written in time order; cumulative distance is
/// summed within each segment only (the stopped gap between sessions is not counted as travel).
/// Positions are semicircles; speed (m/s) and altitude (m) are passed in real units and scaled by
/// the SDK. Timestamps must be UTC — Insta360 syncs by absolute time.
/// </summary>
public static class FitWriter
{
    private const double SemicirclesPerDegree = 2147483648.0 / 180.0; // 2^31 / 180

    public static void Write(string path, IReadOnlyList<IReadOnlyList<TrackPoint>> segments, string sessionName)
    {
        var encoder = new Encode(ProtocolVersion.V20);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        encoder.Open(fs);

        var all = segments.SelectMany(s => s).OrderBy(p => p.Utc).ToList();
        System.DateTime startUtc = all.Count > 0 ? Utc(all[0].Utc) : System.DateTime.UtcNow;
        System.DateTime endUtc = all.Count > 0 ? Utc(all[^1].Utc) : startUtc;

        // file_id — must be the first message.
        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Development);
        fileId.SetProduct(1);
        fileId.SetSerialNumber(0x10101010u);
        fileId.SetTimeCreated(new Dynastream.Fit.DateTime(startUtc));
        encoder.Write(fileId);

        // record messages — distance accumulates within a segment, resets across the stopped gap.
        double distanceM = 0;
        bool haveStart = false;
        int startLatSc = 0, startLonSc = 0;
        foreach (var seg in segments)
        {
            TrackPoint? prev = null;
            foreach (var p in seg)
            {
                if (prev is not null) distanceM += Haversine(prev.Lat, prev.Lon, p.Lat, p.Lon);
                var r = new RecordMesg();
                r.SetTimestamp(new Dynastream.Fit.DateTime(Utc(p.Utc)));
                r.SetPositionLat(Semicircles(p.Lat));
                r.SetPositionLong(Semicircles(p.Lon));
                if (p.EleM is double ele) r.SetAltitude((float)ele);
                if (p.SpeedMs is double spd) r.SetSpeed((float)spd);
                r.SetDistance((float)distanceM);
                encoder.Write(r);

                if (!haveStart) { startLatSc = Semicircles(p.Lat); startLonSc = Semicircles(p.Lon); haveStart = true; }
                prev = p;
            }
        }

        float elapsed = (float)(endUtc - startUtc).TotalSeconds;

        var lap = new LapMesg();
        lap.SetMessageIndex(0);
        lap.SetTimestamp(new Dynastream.Fit.DateTime(endUtc));
        lap.SetStartTime(new Dynastream.Fit.DateTime(startUtc));
        lap.SetTotalElapsedTime(elapsed);
        lap.SetTotalTimerTime(elapsed);
        lap.SetTotalDistance((float)distanceM);
        if (haveStart) { lap.SetStartPositionLat(startLatSc); lap.SetStartPositionLong(startLonSc); }
        lap.SetEvent(Event.Lap);
        lap.SetEventType(EventType.Stop);
        encoder.Write(lap);

        var session = new SessionMesg();
        session.SetMessageIndex(0);
        session.SetTimestamp(new Dynastream.Fit.DateTime(endUtc));
        session.SetStartTime(new Dynastream.Fit.DateTime(startUtc));
        session.SetTotalElapsedTime(elapsed);
        session.SetTotalTimerTime(elapsed);
        session.SetTotalDistance((float)distanceM);
        session.SetSport(Sport.Driving);
        session.SetSubSport(SubSport.Generic);
        session.SetFirstLapIndex(0);
        session.SetNumLaps(1);
        if (haveStart) { session.SetStartPositionLat(startLatSc); session.SetStartPositionLong(startLonSc); }
        session.SetEvent(Event.Session);
        session.SetEventType(EventType.Stop);
        session.SetTrigger(SessionTrigger.ActivityEnd);
        encoder.Write(session);

        var activity = new ActivityMesg();
        activity.SetTimestamp(new Dynastream.Fit.DateTime(endUtc));
        activity.SetTotalTimerTime(elapsed);
        activity.SetNumSessions(1);
        activity.SetType(Activity.Manual);
        activity.SetEvent(Event.Activity);
        activity.SetEventType(EventType.Stop);
        encoder.Write(activity);

        encoder.Close();
    }

    private static int Semicircles(double deg) => (int)System.Math.Round(deg * SemicirclesPerDegree);

    private static System.DateTime Utc(System.DateTime dt) =>
        dt.Kind == System.DateTimeKind.Utc ? dt : System.DateTime.SpecifyKind(dt, System.DateTimeKind.Utc);

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000.0; // metres
        double dLat = ToRad(lat2 - lat1), dLon = ToRad(lon2 - lon1);
        double a = System.Math.Sin(dLat / 2) * System.Math.Sin(dLat / 2)
                 + System.Math.Cos(ToRad(lat1)) * System.Math.Cos(ToRad(lat2))
                 * System.Math.Sin(dLon / 2) * System.Math.Sin(dLon / 2);
        return R * 2 * System.Math.Atan2(System.Math.Sqrt(a), System.Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * System.Math.PI / 180.0;
}
