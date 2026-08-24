using System.Globalization;
using System.Xml;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Routes;

public sealed class GpxRouteParser : IGpxRouteParser
{
    private const long MaximumBytes = 50L * 1024 * 1024;
    private const int MaximumTrackPoints = 250_000;

    public async Task<ParsedGpxRoute> ParseAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };

        var points = new List<GeoPoint>();
        var name = "Unnamed route";

        try
        {
            await using var bounded = new CountingStream(input, MaximumBytes);
            using var reader = XmlReader.Create(bounded, settings);
            var hasNode = await reader.ReadAsync();
            while (hasNode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var advance = true;
                if (reader.NodeType != XmlNodeType.Element)
                {
                    hasNode = await reader.ReadAsync();
                    continue;
                }

                if (reader.LocalName == "name")
                {
                    var candidate = await reader.ReadElementContentAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        name = candidate.Trim();
                    }

                    advance = false;
                }
                else if (reader.LocalName == "trkpt")
                {
                    if (points.Count >= MaximumTrackPoints)
                    {
                        throw new RouteInputException("The GPX route contains more than 250,000 track points.");
                    }

                    points.Add(await ReadTrackPointAsync(reader, cancellationToken));
                    advance = false;
                }

                if (advance)
                {
                    hasNode = await reader.ReadAsync();
                }
            }
        }
        catch (RouteInputException)
        {
            throw;
        }
        catch (XmlException exception)
        {
            throw new RouteInputException("The GPX document is malformed or contains prohibited XML.", exception);
        }

        if (points.Count < 2)
        {
            throw new RouteInputException("A prediction GPX requires at least two elevation-bearing track points.");
        }

        return new ParsedGpxRoute(name, points);
    }

    private static async Task<GeoPoint> ReadTrackPointAsync(XmlReader reader, CancellationToken cancellationToken)
    {
        var latitude = ParseCoordinate(reader.GetAttribute("lat"), "latitude");
        var longitude = ParseCoordinate(reader.GetAttribute("lon"), "longitude");
        var trackPointDepth = reader.Depth;
        string? elevationText = null;

        if (!reader.IsEmptyElement)
        {
            var hasNode = await reader.ReadAsync();
            while (hasNode)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == trackPointDepth && reader.LocalName == "trkpt")
                {
                    break;
                }

                if (reader.NodeType == XmlNodeType.Element && reader.Depth == trackPointDepth + 1 && reader.LocalName == "ele")
                {
                    elevationText = await reader.ReadElementContentAsStringAsync();
                    continue;
                }

                hasNode = await reader.ReadAsync();
            }
        }

        if (!double.TryParse(elevationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var elevation) || !double.IsFinite(elevation))
        {
            throw new RouteInputException("Every GPX track point requires valid elevation.");
        }

        return new GeoPoint(latitude, longitude, elevation);
    }

    private static double ParseCoordinate(string? value, string label)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
        {
            throw new RouteInputException($"A GPX {label} value is invalid.");
        }

        return parsed;
    }

    private sealed class CountingStream(Stream inner, long maximumBytes) : Stream
    {
        private long bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => bytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Count(await inner.ReadAsync(buffer, cancellationToken));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int count)
        {
            bytesRead += count;
            if (bytesRead > maximumBytes)
            {
                throw new RouteInputException("The GPX upload exceeds 50 MB.");
            }

            return count;
        }
    }
}
