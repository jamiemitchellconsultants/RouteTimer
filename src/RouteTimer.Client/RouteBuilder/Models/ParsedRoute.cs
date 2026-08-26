namespace RouteTimer.Client.RouteBuilder.Models;

public sealed record BlobCoordinate(double Lat, double Lng);

public sealed record ParsedRoute(
    RouteWaypoint Origin,
    IReadOnlyList<RouteWaypoint> Intermediates,
    RouteWaypoint? Destination,
    TravelMode Mode,
    string SourceUrl,
    IReadOnlyList<BlobCoordinate> DataBlobCoordinates)
{
    public bool IsSinglePoint => Destination is null;
}
