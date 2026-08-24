using System.Text;

namespace RouteTimer.Services.Tests.Routes;

internal static class GpxFixtures
{
    public static MemoryStream Route((double Latitude, double Longitude, double Elevation) first, (double Latitude, double Longitude, double Elevation) second)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
              <metadata><name>Prediction route</name></metadata>
              <trk><name>Test route</name><trkseg>
                <trkpt lat="{first.Latitude}" lon="{first.Longitude}"><ele>{first.Elevation}</ele></trkpt>
                <trkpt lat="{second.Latitude}" lon="{second.Longitude}"><ele>{second.Elevation}</ele></trkpt>
              </trkseg></trk>
            </gpx>
            """;
        return new MemoryStream(Encoding.UTF8.GetBytes(xml));
    }

    public static MemoryStream WithDoctype() => new(Encoding.UTF8.GetBytes("""
        <!DOCTYPE gpx [ <!ENTITY secret SYSTEM "file:///etc/passwd"> ]>
        <gpx><trk><trkseg><trkpt lat="51" lon="-2"><ele>10</ele></trkpt><trkpt lat="51.01" lon="-2"><ele>11</ele></trkpt></trkseg></trk></gpx>
        """));
}
