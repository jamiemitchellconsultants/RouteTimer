namespace RouteTimer.Client.RouteBuilder.Models;

public abstract record RouteWaypoint;

public sealed record CoordinateWaypoint(double Lat, double Lng) : RouteWaypoint;

public sealed record PlaceNameWaypoint(string Name) : RouteWaypoint;
