using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using RouteTimer.Client.RouteBuilder.Models;

namespace RouteTimer.Client.RouteBuilder;

public static partial class RouteGpxWriter
{
    private const string Namespace = "http://www.topografix.com/GPX/1/1";

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonFileNameCharacters();

    public static string Write(
        string routeName,
        IReadOnlyList<GpxWaypoint> waypoints,
        IReadOnlyList<RoutePoint> track,
        DateTimeOffset generatedAt)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            // A StringWriter would force a utf-16 declaration, so the document is built in a
            // MemoryStream instead. UTF8Encoding(false) suppresses the byte order mark, which
            // some GPX consumers choke on.
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("gpx", Namespace);
            writer.WriteAttributeString("version", "1.1");
            writer.WriteAttributeString("creator", "RouteTimer");

            writer.WriteStartElement("metadata", Namespace);
            writer.WriteElementString("name", Namespace, routeName);
            writer.WriteElementString("time", Namespace,
                generatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            writer.WriteEndElement();

            foreach (var waypoint in waypoints)
            {
                writer.WriteStartElement("wpt", Namespace);
                WriteCoordinates(writer, waypoint.Lat, waypoint.Lng);
                writer.WriteElementString("name", Namespace, waypoint.Name);
                writer.WriteEndElement();
            }

            if (track.Count > 0)
            {
                writer.WriteStartElement("trk", Namespace);
                writer.WriteElementString("name", Namespace, routeName);
                writer.WriteStartElement("trkseg", Namespace);

                foreach (var point in track)
                {
                    writer.WriteStartElement("trkpt", Namespace);
                    WriteCoordinates(writer, point.Lat, point.Lng);

                    if (point.Elevation is { } elevation)
                        writer.WriteElementString("ele", Namespace,
                            elevation.ToString("F1", CultureInfo.InvariantCulture));

                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return new UTF8Encoding(false).GetString(stream.ToArray());
    }

    private const int MaximumFileNameStemLength = 80;

    public static string SuggestFileName(string routeName)
    {
        var cleaned = NonFileNameCharacters().Replace(routeName, "-").Trim('-');
        if (cleaned.Length > MaximumFileNameStemLength)
            cleaned = cleaned[..MaximumFileNameStemLength].TrimEnd('-');

        return cleaned.Length == 0 ? "route.gpx" : $"{cleaned}.gpx";
    }

    private static void WriteCoordinates(XmlWriter writer, double lat, double lng)
    {
        writer.WriteAttributeString("lat", lat.ToString("F7", CultureInfo.InvariantCulture));
        writer.WriteAttributeString("lon", lng.ToString("F7", CultureInfo.InvariantCulture));
    }
}
