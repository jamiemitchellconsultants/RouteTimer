using RouteTimer.Client.RouteBuilder.Models;
using RouteTimer.Client.RouteBuilder;

namespace RouteTimer.Client.Tests.RouteBuilder;

public class GoogleMapsUrlParserTests
{
    private const string Fixture =
        "https://www.google.com/maps/dir/Westbury+Railway+Station,+Station+Approach,+Westbury+BA13+4HP/" +
        "51.2773385,-2.1027806/51.2573319,-1.9896324/51.2023817,-1.9066952/51.1962937,-1.8221028/" +
        "51.1788841,-1.8229255/51.1776336,-1.819276/@51.1743775,-1.8055721,2118m/" +
        "data=!3m1!1e3!4m14!4m13!1m5!1m1!1s0x4873d58d25b28e3b:0x345018c49b5161aa!2m2!1d-2.1991738!2d51.2664565" +
        "!1m0!1m0!1m0!1m0!1m0!1m0!3e1?entry=tts";

    [Fact]
    public void Fixture_HasNamedOriginFiveIntermediatesAndCoordinateDestination()
    {
        var route = GoogleMapsUrlParser.Parse(Fixture);

        var origin = Assert.IsType<PlaceNameWaypoint>(route.Origin);
        Assert.Equal("Westbury Railway Station, Station Approach, Westbury BA13 4HP", origin.Name);

        Assert.Equal(5, route.Intermediates.Count);

        var destination = Assert.IsType<CoordinateWaypoint>(route.Destination);
        Assert.Equal(51.1776336, destination.Lat, 7);
        Assert.Equal(-1.819276, destination.Lng, 7);
    }

    [Fact]
    public void Fixture_PreservesIntermediateOrder()
    {
        var route = GoogleMapsUrlParser.Parse(Fixture);
        var first = Assert.IsType<CoordinateWaypoint>(route.Intermediates[0]);
        var last = Assert.IsType<CoordinateWaypoint>(route.Intermediates[4]);

        Assert.Equal(51.2773385, first.Lat, 7);
        Assert.Equal(51.1788841, last.Lat, 7);
    }

    [Fact]
    public void Fixture_DiscardsTheViewportSegment()
    {
        var route = GoogleMapsUrlParser.Parse(Fixture);
        var all = new[] { route.Origin }.Concat(route.Intermediates).Append(route.Destination!);

        // 51.1743775 is the @ map-centre latitude, not a waypoint.
        Assert.DoesNotContain(all.OfType<CoordinateWaypoint>(), w => Math.Abs(w.Lat - 51.1743775) < 1e-7);
    }

    [Fact]
    public void Fixture_IsBicycling()
    {
        Assert.Equal(TravelMode.Bicycling, GoogleMapsUrlParser.Parse(Fixture).Mode);
    }

    [Fact]
    public void Fixture_ExtractsDataBlobCoordinatesForCrossCheckWithLongitudeFirst()
    {
        var route = GoogleMapsUrlParser.Parse(Fixture);
        var blob = Assert.Single(route.DataBlobCoordinates);

        // The blob encodes !1d<longitude>!2d<latitude> - reversed from every other pair in the URL.
        Assert.Equal(51.2664565, blob.Lat, 7);
        Assert.Equal(-2.1991738, blob.Lng, 7);
    }

    [Fact]
    public void MissingTravelModeToken_DefaultsToDriving()
    {
        var url = "https://www.google.com/maps/dir/51.5,-0.1/51.6,-0.2/";
        Assert.Equal(TravelMode.Driving, GoogleMapsUrlParser.Parse(url).Mode);
    }

    [Theory]
    [InlineData("!3e0", TravelMode.Driving)]
    [InlineData("!3e1", TravelMode.Bicycling)]
    [InlineData("!3e2", TravelMode.Walking)]
    [InlineData("!3e3", TravelMode.Transit)]
    public void TravelModeTokenIsDecoded(string token, TravelMode expected)
    {
        var url = $"https://www.google.com/maps/dir/51.5,-0.1/51.6,-0.2/data=!4m2{token}";
        Assert.Equal(expected, GoogleMapsUrlParser.Parse(url).Mode);
    }

    [Fact]
    public void ApiV1Form_ExtractsOriginWaypointsAndDestination()
    {
        var url = "https://www.google.com/maps/dir/?api=1&origin=Bath+Spa&destination=Bristol" +
                  "&waypoints=Saltford%7CKeynsham&travelmode=walking";

        var route = GoogleMapsUrlParser.Parse(url);

        Assert.Equal("Bath Spa", Assert.IsType<PlaceNameWaypoint>(route.Origin).Name);
        Assert.Equal("Bristol", Assert.IsType<PlaceNameWaypoint>(route.Destination).Name);
        Assert.Equal(
            new[] { "Saltford", "Keynsham" },
            route.Intermediates.Cast<PlaceNameWaypoint>().Select(w => w.Name));
        Assert.Equal(TravelMode.Walking, route.Mode);
    }

    [Theory]
    [InlineData("https://www.google.com/maps/dir/?api=1&origin=&destination=Bristol")]
    [InlineData("https://www.google.com/maps/dir/?api=1&origin=+&destination=Bristol")]
    [InlineData("https://www.google.com/maps/dir/?api=1&origin=Bath+Spa&destination=")]
    public void EmptyOrWhitespaceOriginOrDestination_IsTreatedAsAbsent(string url)
    {
        Assert.Throws<MapUrlParseException>(() => GoogleMapsUrlParser.Parse(url));
    }

    [Fact]
    public void PlaceUrl_ReturnsOnePointAndNoDestination()
    {
        var url = "https://www.google.com/maps/place/Westbury+White+Horse/@51.2469,-2.1447,15z";
        var route = GoogleMapsUrlParser.Parse(url);

        Assert.True(route.IsSinglePoint);
        Assert.Null(route.Destination);
        Assert.Empty(route.Intermediates);
        Assert.Equal("Westbury White Horse", Assert.IsType<PlaceNameWaypoint>(route.Origin).Name);
    }

    [Fact]
    public void NonComGoogleDomainIsAccepted()
    {
        var url = "https://www.google.co.uk/maps/dir/51.5,-0.1/51.6,-0.2/";
        Assert.Equal(2, CountWaypoints(GoogleMapsUrlParser.Parse(url)));
    }

    [Fact]
    public void EncodedPlusInSegmentBecomesSpaceButEscapedPlusSurvives()
    {
        var url = "https://www.google.com/maps/dir/A+B/C%2BD/51.6,-0.2/";
        var route = GoogleMapsUrlParser.Parse(url);

        Assert.Equal("A B", Assert.IsType<PlaceNameWaypoint>(route.Origin).Name);
        Assert.Equal("C+D", Assert.IsType<PlaceNameWaypoint>(route.Intermediates[0]).Name);
    }

    [Theory]
    [InlineData("https://maps.app.goo.gl/Xj37iafwGZrVmfG77", "Xj37iafwGZrVmfG77")]
    [InlineData("https://goo.gl/maps/abcd1234", "abcd1234")]
    [InlineData("https://maps.app.goo.gl/Xj37iafwGZrVmfG77?g_st=ic", "Xj37iafwGZrVmfG77")]
    [InlineData("https://goo.gl/maps/abcd1234?foo=bar", "abcd1234")]
    public void ShortLinksAreRecognised(string url, string expectedCode)
    {
        Assert.True(GoogleMapsUrlParser.IsShortLink(url, out var code));
        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public void ShortLinkCannotBeParsedDirectly()
    {
        var ex = Assert.Throws<MapUrlParseException>(
            () => GoogleMapsUrlParser.Parse("https://maps.app.goo.gl/Xj37iafwGZrVmfG77"));
        Assert.Contains("short link", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LongUrlIsNotAShortLink()
    {
        Assert.False(GoogleMapsUrlParser.IsShortLink(Fixture, out _));
    }

    [Theory]
    [InlineData("https://google.evil.com/maps/dir/51.5,-0.1/51.6,-0.2/")]
    [InlineData("https://evilgoogle.com/maps/dir/51.5,-0.1/51.6,-0.2/")]
    [InlineData("https://mygoogle.co/maps/dir/51.5,-0.1/51.6,-0.2/")]
    [InlineData("https://xgoogle.net/maps/dir/51.5,-0.1/51.6,-0.2/")]
    [InlineData("https://google.evil.co.uk/maps/dir/51.5,-0.1/51.6,-0.2/")]
    [InlineData("https://google.xyz.com/maps/dir/51.5,-0.1/51.6,-0.2/")]
    [InlineData("https://google.co.com/maps/dir/51.5,-0.1/51.6,-0.2/")]
    public void SpoofedGoogleHostsAreRejected(string url)
    {
        Assert.Throws<MapUrlParseException>(() => GoogleMapsUrlParser.Parse(url));
    }

    [Theory]
    [InlineData("https://www.google.com/maps/dir/51.5,-0.1/51.6,-0.2/")]
    [InlineData("https://www.google.co.uk/maps/dir/51.5,-0.1/51.6,-0.2/")]
    [InlineData("https://maps.google.com/maps/dir/51.5,-0.1/51.6,-0.2/")]
    [InlineData("https://google.com/maps/dir/51.5,-0.1/51.6,-0.2/")]
    public void AuthenticGoogleHostsAreParsed(string url)
    {
        var route = GoogleMapsUrlParser.Parse(url);
        Assert.NotNull(route);
        Assert.Equal(2, CountWaypoints(route));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://www.google.com/maps/dir/a/b")]
    [InlineData("https://example.com/maps/dir/a/b")]
    [InlineData("https://www.google.com/maps/dir/OnlyOneWaypoint/")]
    public void UnusableInputThrows(string url)
    {
        Assert.Throws<MapUrlParseException>(() => GoogleMapsUrlParser.Parse(url));
    }

    private static int CountWaypoints(ParsedRoute route) =>
        1 + route.Intermediates.Count + (route.Destination is null ? 0 : 1);
}
