namespace RouteTimer.Services.Tests.Routes;

internal static class RouteFixtures
{
    public static IReadOnlyList<(double Latitude, double Longitude, double ElevationMetres)> StraightClimb(
        double lengthMetres,
        double riseMetres,
        double noiseMetres)
    {
        const double latitude = 51.0;
        const int pointCount = 21;
        var metresPerLongitudeDegree = 111_320d * Math.Cos(latitude * Math.PI / 180d);
        var points = new List<(double Latitude, double Longitude, double ElevationMetres)>();

        for (var index = 0; index < pointCount; index++)
        {
            var fraction = index / (double)(pointCount - 1);
            var noise = index is 0 or pointCount - 1 ? 0d : (index % 2 == 0 ? noiseMetres : -noiseMetres);
            points.Add((latitude, fraction * lengthMetres / metresPerLongitudeDegree, fraction * riseMetres + noise));
        }

        return points;
    }
}
