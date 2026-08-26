using System.Text.RegularExpressions;
using RouteTimer.Client.RouteBuilder.Models;

namespace RouteTimer.Client.RouteBuilder;

public static partial class GoogleMapsUrlParser
{
    [GeneratedRegex(@"^https?://(?:maps\.app\.goo\.gl|goo\.gl/maps)/([A-Za-z0-9_-]{4,64})/?(?:\?.*)?(?:#.*)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ShortLinkPattern();

    [GeneratedRegex(@"^(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)$")]
    private static partial Regex CoordinatePattern();

    [GeneratedRegex(@"!3e(\d)")]
    private static partial Regex TravelModePattern();

    [GeneratedRegex(@"!2m2!1d(-?\d+(?:\.\d+)?)!2d(-?\d+(?:\.\d+)?)")]
    private static partial Regex BlobCoordinatePattern();

    [GeneratedRegex(@"^google\.[a-z]{2,3}(\.[a-z]{2})?$")]
    private static partial Regex GoogleHostPattern();

    public static bool IsShortLink(string url, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(url)) return false;

        var match = ShortLinkPattern().Match(url.Trim());
        if (!match.Success) return false;

        code = match.Groups[1].Value;
        return true;
    }

    public static ParsedRoute Parse(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new MapUrlParseException("No URL supplied.");

        var trimmed = url.Trim();

        if (IsShortLink(trimmed, out _))
            throw new MapUrlParseException(
                "That is a short link. It must be resolved to its full www.google.com/maps URL first.");

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new MapUrlParseException($"'{trimmed}' is not an absolute http or https URL.");

        var lowercaseHost = uri.Host.ToLowerInvariant();
        var hostToValidate = lowercaseHost;
        if (hostToValidate.StartsWith("www."))
            hostToValidate = hostToValidate["www.".Length..];
        else if (hostToValidate.StartsWith("maps."))
            hostToValidate = hostToValidate["maps.".Length..];

        // Deliberately shallow: the parser does no network I/O, never dereferences the host,
        // and the key is never sent on the basis of it. This is a usability filter that gives
        // a clear error for non-Google URLs, not a trust boundary, so a regex match on the
        // host string is all it needs to be.
        if (!GoogleHostPattern().IsMatch(hostToValidate))
            throw new MapUrlParseException(
                $"'{uri.Host}' is not a Google Maps host. " +
                "Expected something like www.google.com or www.google.co.uk.");

        var path = uri.AbsolutePath;
        var query = ParseQuery(uri.Query);
        var blob = ExtractDataBlob(path);
        var mode = ParseTravelMode(blob, query);
        var blobCoordinates = ParseBlobCoordinates(blob);

        var dirIndex = path.IndexOf("/dir/", StringComparison.OrdinalIgnoreCase);
        if (dirIndex >= 0)
        {
            var waypoints = path[(dirIndex + "/dir/".Length)..]
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(IsWaypointSegment)
                .Select(DecodeSegment)
                .Where(s => s.Length > 0)
                .Select(ToWaypoint)
                .ToList();

            if (waypoints.Count >= 2)
                // List<T> supports the ^ index operator but not the .. range operator,
                // so the intermediates are taken with GetRange rather than a slice.
                return new ParsedRoute(
                    waypoints[0],
                    waypoints.GetRange(1, waypoints.Count - 2),
                    waypoints[^1],
                    mode,
                    trimmed,
                    blobCoordinates);

            if (!query.ContainsKey("origin"))
                throw new MapUrlParseException(
                    $"Found {waypoints.Count} waypoint(s) in the URL path; a route needs at least an origin and a destination.");
        }

        if (query.TryGetValue("origin", out var origin) && !string.IsNullOrWhiteSpace(origin) &&
            query.TryGetValue("destination", out var destination) && !string.IsNullOrWhiteSpace(destination))
        {
            var intermediates = query.TryGetValue("waypoints", out var raw)
                ? raw.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(ToWaypoint).ToList()
                : [];

            return new ParsedRoute(
                ToWaypoint(origin), intermediates, ToWaypoint(destination), mode, trimmed, blobCoordinates);
        }

        var placeIndex = path.IndexOf("/place/", StringComparison.OrdinalIgnoreCase);
        if (placeIndex >= 0)
        {
            var name = path[(placeIndex + "/place/".Length)..]
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(IsWaypointSegment)
                .Select(DecodeSegment)
                .FirstOrDefault(s => s.Length > 0);

            if (name is not null)
                return new ParsedRoute(ToWaypoint(name), [], null, mode, trimmed, blobCoordinates);
        }

        throw new MapUrlParseException(
            "Could not find a route in that URL. Expected a /maps/dir/ link, a /maps/place/ link, " +
            "or a ?api=1&origin=...&destination=... link.");
    }

    private static bool IsWaypointSegment(string segment) =>
        !segment.StartsWith('@') &&
        !segment.StartsWith("data=", StringComparison.OrdinalIgnoreCase);

    // Google writes spaces as '+' in path segments, so '+' must become a space while an
    // escaped %2B stays a literal plus. Rewriting '+' to %20 before unescaping does both.
    private static string DecodeSegment(string segment) =>
        Uri.UnescapeDataString(segment.Replace("+", "%20")).Trim();

    private static RouteWaypoint ToWaypoint(string segment)
    {
        var match = CoordinatePattern().Match(segment);
        return match.Success
            ? new CoordinateWaypoint(
                double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
            : new PlaceNameWaypoint(segment);
    }

    private static string ExtractDataBlob(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(s => s.StartsWith("data=", StringComparison.OrdinalIgnoreCase))?["data=".Length..]
        ?? "";

    private static TravelMode ParseTravelMode(string blob, IReadOnlyDictionary<string, string> query)
    {
        var match = TravelModePattern().Match(blob);
        if (match.Success)
            return match.Groups[1].Value switch
            {
                "1" => TravelMode.Bicycling,
                "2" => TravelMode.Walking,
                "3" => TravelMode.Transit,
                _ => TravelMode.Driving
            };

        if (query.TryGetValue("travelmode", out var named))
            return named.ToLowerInvariant() switch
            {
                "bicycling" => TravelMode.Bicycling,
                "walking" => TravelMode.Walking,
                "transit" => TravelMode.Transit,
                _ => TravelMode.Driving
            };

        return TravelMode.Driving;
    }

    // Cross-check only. The blob encodes !1d<longitude>!2d<latitude>, reversed relative to
    // every other coordinate pair in the URL, and the encoding is undocumented.
    private static List<BlobCoordinate> ParseBlobCoordinates(string blob) =>
        BlobCoordinatePattern().Matches(blob)
            .Select(m => new BlobCoordinate(
                double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace("+", "%20")) : "";
            result[key] = value;
        }

        return result;
    }
}
