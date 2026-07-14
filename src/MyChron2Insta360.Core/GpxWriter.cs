using System.Globalization;
using System.Text;
using System.Xml;

namespace MyChron2Insta360.Core;

/// <summary>
/// Writes a GPX 1.1 track. Insta360 derives speed/heading from position+time deltas, so accuracy
/// lives in lat/lon/time; the Garmin TrackPointExtension (speed m/s, course deg) is written as a
/// bonus. Timestamps are UTC with millisecond precision (required above 1 Hz so points don't collide).
/// </summary>
public static class GpxWriter
{
    private const string Gpx = "http://www.topografix.com/GPX/1/1";
    private const string Tpx = "http://www.garmin.com/xmlschemas/TrackPointExtension/v2";
    private const string Xmlns = "http://www.w3.org/2000/xmlns/";

    public static void Write(string path, IReadOnlyList<TrackPoint> points, string trackName,
                             string creator = "MyChron-GPS-to-Insta360")
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var w = XmlWriter.Create(path, settings);

        w.WriteStartDocument();
        w.WriteStartElement("gpx", Gpx);
        w.WriteAttributeString("version", "1.1");
        w.WriteAttributeString("creator", creator);
        w.WriteAttributeString("xmlns", "gpxtpx", Xmlns, Tpx);

        w.WriteStartElement("metadata", Gpx);
        if (points.Count > 0) w.WriteElementString("time", Gpx, Iso(points[0].Utc));
        w.WriteEndElement();

        w.WriteStartElement("trk", Gpx);
        w.WriteElementString("name", Gpx, trackName);
        w.WriteStartElement("trkseg", Gpx);

        foreach (var p in points)
        {
            w.WriteStartElement("trkpt", Gpx);
            w.WriteAttributeString("lat", p.Lat.ToString("0.#######", CultureInfo.InvariantCulture));
            w.WriteAttributeString("lon", p.Lon.ToString("0.#######", CultureInfo.InvariantCulture));

            if (p.EleM is double ele)
                w.WriteElementString("ele", Gpx, ele.ToString("0.##", CultureInfo.InvariantCulture));
            w.WriteElementString("time", Gpx, Iso(p.Utc));

            if (p.SpeedMs is double || p.HeadingDeg is double)
            {
                w.WriteStartElement("extensions", Gpx);
                w.WriteStartElement("gpxtpx", "TrackPointExtension", Tpx);
                if (p.SpeedMs is double spd)
                    w.WriteElementString("gpxtpx", "speed", Tpx, spd.ToString("0.###", CultureInfo.InvariantCulture));
                if (p.HeadingDeg is double hdg)
                    w.WriteElementString("gpxtpx", "course", Tpx, hdg.ToString("0.###", CultureInfo.InvariantCulture));
                w.WriteEndElement(); // TrackPointExtension
                w.WriteEndElement(); // extensions
            }

            w.WriteEndElement(); // trkpt
        }

        w.WriteEndElement(); // trkseg
        w.WriteEndElement(); // trk
        w.WriteEndElement(); // gpx
        w.WriteEndDocument();
    }

    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
