using Dynastream.Fit;

namespace MyChron2Insta360.Core;

/// <summary>
/// Writes a minimal but valid Garmin FIT Activity file (file_id -> records -> lap -> session ->
/// activity). Records from all session segments are written in time order; cumulative distance is
/// summed within each segment only (the stopped gap between sessions is not counted as travel).
/// Positions are semicircles; speed (m/s) and altitude (m) are passed in real units and scaled by
/// the SDK. Timestamps must be UTC, Insta360 syncs by absolute time.
/// </summary>
public static class FitWriter
{
    private const double SemicirclesPerDegree = 2147483648.0 / 180.0; // 2^31 / 180
    private const int ImuBatch = 30; // samples per accelerometer_data / gyroscope_data message

    /// <summary>Optional extra streams folded into the FIT alongside the GPS track.</summary>
    public sealed class Extras
    {
        /// <summary>Inertial samples (accel g, gyro deg/s) written as calibrated sensor messages.</summary>
        public IReadOnlyList<ImuSample>? Imu { get; init; }
        /// <summary>Lap splits from the logger, written as native lap messages.</summary>
        public IReadOnlyList<LapSplit>? Laps { get; init; }
        /// <summary>Temperature samples (UTC, degrees C) merged into the nearest record message.</summary>
        public IReadOnlyList<(System.DateTime Utc, double C)>? Temperature { get; init; }
    }

    public static void Write(string path, IReadOnlyList<IReadOnlyList<TrackPoint>> segments, string sessionName)
        => Write(path, segments, sessionName, null);

    public static void Write(string path, IReadOnlyList<IReadOnlyList<TrackPoint>> segments, string sessionName, Extras? extras)
    {
        var encoder = new Encode(ProtocolVersion.V20);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        encoder.Open(fs);

        var all = segments.SelectMany(s => s).OrderBy(p => p.Utc).ToList();
        System.DateTime startUtc = all.Count > 0 ? Utc(all[0].Utc) : System.DateTime.UtcNow;
        System.DateTime endUtc = all.Count > 0 ? Utc(all[^1].Utc) : startUtc;

        // file_id, must be the first message.
        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Development);
        fileId.SetProduct(1);
        fileId.SetSerialNumber(0x10101010u);
        fileId.SetTimeCreated(new Dynastream.Fit.DateTime(startUtc));
        encoder.Write(fileId);

        var temps = extras?.Temperature;

        // record messages, distance accumulates within a segment, resets across the stopped gap.
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
                if (temps is not null && temps.Count > 0)
                {
                    double? c = NearestTemp(temps, p.Utc);
                    if (c is double tc) r.SetTemperature((sbyte)Math.Round(Math.Clamp(tc, -128, 127)));
                }
                encoder.Write(r);

                if (!haveStart) { startLatSc = Semicircles(p.Lat); startLonSc = Semicircles(p.Lon); haveStart = true; }
                prev = p;
            }
        }

        float elapsed = (float)(endUtc - startUtc).TotalSeconds;

        // IMU: batched calibrated sensor messages (accel in g, gyro in deg/s), timestamped per batch
        // with a per-sample millisecond offset, which is how FIT models high-rate sensor streams.
        if (extras?.Imu is { Count: > 0 } imu) WriteImu(encoder, imu, startUtc);

        // Laps: real per-lap messages when the logger provided splits, else one lap for the whole activity.
        int lapCount = 0;
        if (extras?.Laps is { Count: > 0 } laps)
        {
            foreach (var l in laps)
            {
                var lm = new LapMesg();
                lm.SetMessageIndex((ushort)lapCount);
                lm.SetStartTime(new Dynastream.Fit.DateTime(Utc(l.StartUtc)));
                lm.SetTimestamp(new Dynastream.Fit.DateTime(Utc(l.StartUtc.AddSeconds(l.DurationS))));
                lm.SetTotalElapsedTime((float)l.DurationS);
                lm.SetTotalTimerTime((float)l.DurationS);
                lm.SetEvent(Event.Lap);
                lm.SetEventType(EventType.Stop);
                encoder.Write(lm);
                lapCount++;
            }
        }
        else
        {
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
            lapCount = 1;
        }

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
        session.SetNumLaps((ushort)Math.Max(1, lapCount));
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

    /// <summary>
    /// Writes the IMU as FIT accelerometer_data / gyroscope_data messages. Each message carries a batch
    /// of samples: the message timestamp is the batch's absolute second, timestamp_ms its millisecond,
    /// and each sample gets a millisecond sample_time_offset. Values are the calibrated fields, so they
    /// stay in real units (g and deg/s) with no scale/offset bookkeeping.
    /// </summary>
    private static void WriteImu(Encode encoder, IReadOnlyList<ImuSample> imu, System.DateTime fallbackStart)
    {
        System.DateTime At(ImuSample s) => Utc(s.Utc ?? fallbackStart.AddSeconds(s.ElapsedS));
        for (int i = 0, n; i < imu.Count; i += n)
        {
            System.DateTime t0 = At(imu[i]);
            // A batch also ends where the next sample is out of reach of its 16-bit millisecond offset,
            // across the stop between two merged sessions for instance, rather than be clamped to it.
            n = 1;
            while (n < ImuBatch && i + n < imu.Count
                   && (At(imu[i + n]) - t0).TotalMilliseconds is >= 0 and <= 65535)
                n++;

            var a = new AccelerometerDataMesg();
            a.SetTimestamp(new Dynastream.Fit.DateTime(t0));
            a.SetTimestampMs((ushort)t0.Millisecond);
            var g = new GyroscopeDataMesg();
            g.SetTimestamp(new Dynastream.Fit.DateTime(t0));
            g.SetTimestampMs((ushort)t0.Millisecond);

            for (int k = 0; k < n; k++)
            {
                var s = imu[i + k];
                System.DateTime tk = Utc(s.Utc ?? fallbackStart.AddSeconds(s.ElapsedS));
                ushort off = (ushort)System.Math.Clamp((tk - t0).TotalMilliseconds, 0, 65535);

                a.SetSampleTimeOffset(k, off);
                a.SetCalibratedAccelX(k, (float)s.Ax);
                a.SetCalibratedAccelY(k, (float)s.Ay);
                a.SetCalibratedAccelZ(k, (float)s.Az);

                g.SetSampleTimeOffset(k, off);
                g.SetCalibratedGyroX(k, (float)s.Gx);
                g.SetCalibratedGyroY(k, (float)s.Gy);
                g.SetCalibratedGyroZ(k, (float)s.Gz);
            }
            encoder.Write(a);
            encoder.Write(g);
        }
    }

    /// <summary>Temperature nearest the given time, if within 30 s (the samples are ~1 Hz).</summary>
    private static double? NearestTemp(IReadOnlyList<(System.DateTime Utc, double C)> temps, System.DateTime at)
    {
        int lo = 0, hi = temps.Count - 1, best = -1;
        double bestDiff = double.MaxValue;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            double diff = System.Math.Abs((temps[mid].Utc - at).TotalSeconds);
            if (diff < bestDiff) { bestDiff = diff; best = mid; }
            if (temps[mid].Utc < at) lo = mid + 1; else hi = mid - 1;
        }
        return best >= 0 && bestDiff <= 30 ? temps[best].C : null;
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
