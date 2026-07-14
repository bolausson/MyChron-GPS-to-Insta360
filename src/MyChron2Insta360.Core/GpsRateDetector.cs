namespace MyChron2Insta360.Core;

/// <summary>
/// Recovers the true GPS fix rate hidden in an AiM log. The logger records at a high rate (100 Hz,
/// the IMU rate) but the GNSS receiver is slower (e.g. 10 Hz on MyChron5, 25 Hz on MyChron5S/6);
/// RaceStudio3 smoothly up-samples the GPS between real fixes. That leaves the position track's
/// curvature (its 2nd difference) periodic at the fix interval. We find that period by measuring,
/// for each candidate period P, how concentrated the curvature energy is at a single phase (i mod P).
/// A real period shows a strong spike; unrelated periods look flat. The fundamental is the smallest
/// period whose score is near the peak (harmonics sit at its multiples).
/// </summary>
public static class GpsRateDetector
{
    private const int MaxPeriod = 30;
    private const double MovingSpeedKmh = 15.0;   // ignore near-stationary rows (GPS jitter, no real heading)
    private const int MinMovingRows = 500;        // need enough signal to be confident
    private const double PeriodicityFloor = 1.5;  // below this, no clear structure -> already at logging rate

    /// <summary>Detected GPS rate in Hz, or null if no clear sub-rate structure was found.</summary>
    public static double? Detect(AimCsvData data, int iTime, int iSpd, int iLat, int iLon, double loggingHz)
    {
        var rows = data.Rows;
        int n = rows.Count;
        if (n < MinMovingRows + 2 || iLat < 0 || iLon < 0) return null;

        // Curvature magnitude per row (|2nd difference| of position), moving rows only.
        var dd = new double[n];
        int moving = 0;
        for (int i = 1; i < n - 1; i++)
        {
            if (iSpd >= 0)
            {
                double sp = rows[i][iSpd];
                if (double.IsNaN(sp) || sp <= MovingSpeedKmh) continue;
            }
            double la0 = rows[i - 1][iLat], la1 = rows[i][iLat], la2 = rows[i + 1][iLat];
            double lo0 = rows[i - 1][iLon], lo1 = rows[i][iLon], lo2 = rows[i + 1][iLon];
            if (double.IsNaN(la0) || double.IsNaN(la1) || double.IsNaN(la2) ||
                double.IsNaN(lo0) || double.IsNaN(lo1) || double.IsNaN(lo2)) continue;

            double ddLa = la2 - 2 * la1 + la0;
            double ddLo = lo2 - 2 * lo1 + lo0;
            dd[i] = Math.Sqrt(ddLa * ddLa + ddLo * ddLo);
            moving++;
        }
        if (moving < MinMovingRows) return null;

        var score = new double[MaxPeriod + 1];
        double best = 0;
        for (int p = 2; p <= MaxPeriod; p++)
        {
            var bins = new double[p];
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                double v = dd[i];
                if (v > 0) { bins[i % p] += v; total += v; }
            }
            if (total <= 0) continue;
            double max = 0;
            foreach (double b in bins) if (b > max) max = b;
            score[p] = (max / total) * p;   // 1.0 == uniform (no periodicity); >1 == concentrated
            if (score[p] > best) best = score[p];
        }

        if (best < PeriodicityFloor) return null; // GPS is effectively at the logging rate

        int fundamental = 0;
        for (int p = 2; p <= MaxPeriod; p++)
            if (score[p] >= 0.9 * best) { fundamental = p; break; } // smallest near-peak period
        if (fundamental <= 0) return null;

        return loggingHz / fundamental;
    }
}
