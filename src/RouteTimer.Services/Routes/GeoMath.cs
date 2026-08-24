using RouteTimer.Domain.Routes;

namespace RouteTimer.Services.Routes;

internal static class GeoMath
{
    private const double EarthRadiusMetres = 6_371_000;

    public static double DistanceMetres(GeoPoint first, GeoPoint second)
    {
        var latitudeDifference = DegreesToRadians(second.Latitude - first.Latitude);
        var longitudeDifference = DegreesToRadians(second.Longitude - first.Longitude);
        var latitude1 = DegreesToRadians(first.Latitude);
        var latitude2 = DegreesToRadians(second.Latitude);
        var sineLatitude = Math.Sin(latitudeDifference / 2);
        var sineLongitude = Math.Sin(longitudeDifference / 2);
        var a = sineLatitude * sineLatitude + Math.Cos(latitude1) * Math.Cos(latitude2) * sineLongitude * sineLongitude;
        return EarthRadiusMetres * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static double HeadingRadians(GeoPoint first, GeoPoint second)
    {
        var latitude1 = DegreesToRadians(first.Latitude);
        var latitude2 = DegreesToRadians(second.Latitude);
        var longitudeDifference = DegreesToRadians(second.Longitude - first.Longitude);
        var y = Math.Sin(longitudeDifference) * Math.Cos(latitude2);
        var x = Math.Cos(latitude1) * Math.Sin(latitude2) - Math.Sin(latitude1) * Math.Cos(latitude2) * Math.Cos(longitudeDifference);
        return Math.Atan2(y, x);
    }

    public static double NormalizeRadians(double value)
    {
        while (value > Math.PI)
        {
            value -= 2 * Math.PI;
        }

        while (value < -Math.PI)
        {
            value += 2 * Math.PI;
        }

        return value;
    }

    public static GeoPoint Interpolate(GeoPoint first, GeoPoint second, double fraction) => new(
        first.Latitude + ((second.Latitude - first.Latitude) * fraction),
        first.Longitude + ((second.Longitude - first.Longitude) * fraction),
        first.ElevationMetres + ((second.ElevationMetres - first.ElevationMetres) * fraction));

    private static double DegreesToRadians(double value) => value * Math.PI / 180d;
}
