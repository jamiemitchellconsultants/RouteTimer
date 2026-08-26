using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Routes;

public sealed class PredictionNotCompleteException()
    : Exception("The prediction has no route segments, so it cannot be exported.");

public sealed record PredictionGpxSource(
    string RouteName,
    string Description,
    DateTimeOffset GeneratedAt,
    DateTimeOffset StartAt,
    IReadOnlyList<PersistedPredictionSegment> Segments);

public static partial class PredictionGpxWriter
{
    private const string Namespace = "http://www.topografix.com/GPX/1/1";
    private const int MaximumFileNameStemLength = 80;

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonFileNameCharacters();

    public static string Write(PredictionGpxSource source, bool timed)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Segments.Count == 0)
        {
            throw new PredictionNotCompleteException();
        }

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            // A StringWriter would force a utf-16 declaration, so the document is built in a
            // MemoryStream. UTF8Encoding(false) suppresses the byte order mark, which some GPX
            // consumers choke on.
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
            writer.WriteElementString("name", Namespace, source.RouteName);
            writer.WriteElementString("desc", Namespace, source.Description);
            writer.WriteElementString("time", Namespace, Instant(source.GeneratedAt));
            writer.WriteEndElement();

            writer.WriteStartElement("trk", Namespace);
            writer.WriteElementString("name", Namespace, source.RouteName);
            writer.WriteStartElement("trkseg", Namespace);

            foreach (var segment in source.Segments.OrderBy(segment => segment.Sequence))
            {
                writer.WriteStartElement("trkpt", Namespace);
                writer.WriteAttributeString("lat", segment.Latitude.ToString("F7", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("lon", segment.Longitude.ToString("F7", CultureInfo.InvariantCulture));
                writer.WriteElementString("ele", Namespace, segment.ElevationMetres.ToString("F1", CultureInfo.InvariantCulture));

                // Times are opt-in: several course importers treat a timestamped track as an
                // activity rather than a route, so the variant Garmin receives carries none.
                if (timed)
                {
                    writer.WriteElementString("time", Namespace, Instant(source.StartAt + segment.CumulativeMovingTime));
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return new UTF8Encoding(false).GetString(stream.ToArray());
    }

    public static string SuggestFileName(string routeName)
    {
        var cleaned = NonFileNameCharacters().Replace(routeName, "-").Trim('-');
        if (cleaned.Length > MaximumFileNameStemLength)
        {
            cleaned = cleaned[..MaximumFileNameStemLength].TrimEnd('-');
        }

        return cleaned.Length == 0 ? "route.gpx" : $"{cleaned}.gpx";
    }

    private static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
