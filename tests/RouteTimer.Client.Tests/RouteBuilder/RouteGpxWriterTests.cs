using System.Xml.Linq;
using RouteTimer.Client.RouteBuilder;
using RouteTimer.Client.RouteBuilder.Models;

namespace RouteTimer.Client.Tests.RouteBuilder;

public class RouteGpxWriterTests
{
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";
    private static readonly DateTimeOffset At = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly GpxWaypoint[] Waypoints =
    [
        new(51.2664565, -2.1991738, "Westbury Railway Station"),
        new(51.1776336, -1.819276, "Finish")
    ];

    private static readonly RoutePoint[] Track =
    [
        new(51.2664565, -2.1991738, 42.5),
        new(51.2773385, -2.1027806, 88.0),
        new(51.1776336, -1.819276, 101.44)
    ];

    private static XDocument Parse(string gpx) => XDocument.Parse(gpx);

    [Fact]
    public void ProducesWellFormedGpx11WithTheCorrectNamespace()
    {
        var doc = Parse(RouteGpxWriter.Write("Westbury ride", Waypoints, Track, At));

        Assert.Equal(Gpx + "gpx", doc.Root!.Name);
        Assert.Equal("1.1", doc.Root.Attribute("version")!.Value);
        Assert.Equal("RouteTimer", doc.Root.Attribute("creator")!.Value);
    }

    [Fact]
    public void MetadataCarriesNameAndUtcTime()
    {
        var doc = Parse(RouteGpxWriter.Write("Westbury ride", Waypoints, Track, At));
        var metadata = doc.Root!.Element(Gpx + "metadata")!;

        Assert.Equal("Westbury ride", metadata.Element(Gpx + "name")!.Value);
        Assert.Equal("2026-08-23T12:00:00Z", metadata.Element(Gpx + "time")!.Value);
    }

    [Fact]
    public void EachWaypointBecomesANamedWptElement()
    {
        var doc = Parse(RouteGpxWriter.Write("Westbury ride", Waypoints, Track, At));
        var wpts = doc.Root!.Elements(Gpx + "wpt").ToList();

        Assert.Equal(2, wpts.Count);
        Assert.Equal("Westbury Railway Station", wpts[0].Element(Gpx + "name")!.Value);
        Assert.Equal("51.2664565", wpts[0].Attribute("lat")!.Value);
        Assert.Equal("-2.1991738", wpts[0].Attribute("lon")!.Value);
    }

    [Fact]
    public void TrackHasOneSegmentWithEveryPointInOrder()
    {
        var doc = Parse(RouteGpxWriter.Write("Westbury ride", Waypoints, Track, At));
        var segments = doc.Root!.Element(Gpx + "trk")!.Elements(Gpx + "trkseg").ToList();
        var points = Assert.Single(segments).Elements(Gpx + "trkpt").ToList();

        Assert.Equal(3, points.Count);
        Assert.Equal("51.2664565", points[0].Attribute("lat")!.Value);
        Assert.Equal("51.1776336", points[2].Attribute("lat")!.Value);
    }

    [Fact]
    public void ElevationIsWrittenToOneDecimalPlaceWhenPresent()
    {
        var doc = Parse(RouteGpxWriter.Write("Westbury ride", Waypoints, Track, At));
        var points = doc.Root!.Descendants(Gpx + "trkpt").ToList();

        Assert.Equal("42.5", points[0].Element(Gpx + "ele")!.Value);
        Assert.Equal("101.4", points[2].Element(Gpx + "ele")!.Value);
    }

    [Fact]
    public void ElevationElementIsOmittedEntirelyWhenAbsent()
    {
        RoutePoint[] flat = [new(51.5, -0.1), new(51.6, -0.2)];
        var doc = Parse(RouteGpxWriter.Write("No elevation", Waypoints, flat, At));

        Assert.Empty(doc.Root!.Descendants(Gpx + "ele"));
    }

    [Fact]
    public void CoordinatesUseSevenDecimalPlacesAndInvariantCulture()
    {
        RoutePoint[] track = [new(51.5, -0.1), new(51.6, -0.2)];
        var doc = Parse(RouteGpxWriter.Write("Precision", [], track, At));
        var first = doc.Root!.Descendants(Gpx + "trkpt").First();

        Assert.Equal("51.5000000", first.Attribute("lat")!.Value);
        Assert.Equal("-0.1000000", first.Attribute("lon")!.Value);
    }

    [Fact]
    public void NamesContainingMarkupAreEscaped()
    {
        GpxWaypoint[] awkward = [new(51.5, -0.1, "Bath & Wells <start>")];
        var xml = RouteGpxWriter.Write("A & B", awkward, Track, At);

        Assert.DoesNotContain("Bath & Wells <start>", xml);

        var doc = Parse(xml);
        Assert.Equal("Bath & Wells <start>", doc.Root!.Element(Gpx + "wpt")!.Element(Gpx + "name")!.Value);
    }

    [Fact]
    public void OutputHasNoByteOrderMark()
    {
        var xml = RouteGpxWriter.Write("Westbury ride", Waypoints, Track, At);
        Assert.NotEqual('﻿', xml[0]);
    }

    [Fact]
    public void EmptyTrackStillProducesAValidDocumentWithNoTrkElement()
    {
        var doc = Parse(RouteGpxWriter.Write("Single point", Waypoints, [], At));

        Assert.Null(doc.Root!.Element(Gpx + "trk"));
        Assert.Equal(2, doc.Root.Elements(Gpx + "wpt").Count());
    }

    [Theory]
    [InlineData("Westbury ride", "Westbury-ride.gpx")]
    [InlineData("Bath / Wells: a ride?", "Bath-Wells-a-ride.gpx")]
    [InlineData("   ", "route.gpx")]
    public void FileNameIsDerivedAndSanitised(string routeName, string expected)
    {
        Assert.Equal(expected, RouteGpxWriter.SuggestFileName(routeName));
    }

    [Fact]
    public void FileNameStemIsTruncatedTo80CharactersTrimmingATrailingHyphenLeftByTheCut()
    {
        // The space at position 79 becomes a single hyphen after cleaning, which then lands
        // exactly on the 80-character cut and must be trimmed rather than left dangling.
        var routeName = new string('A', 79) + " " + new string('B', 20);

        var fileName = RouteGpxWriter.SuggestFileName(routeName);

        Assert.Equal(new string('A', 79) + ".gpx", fileName);
    }

    [Fact]
    public void Every_track_point_carries_elevation_when_elevation_is_present()
    {
        var track = new[]
        {
            new RoutePoint(51.5000000, -0.1000000, 12.3),
            new RoutePoint(51.5010000, -0.1010000, 15.8)
        };

        var gpx = RouteGpxWriter.Write("Test route", [], track, DateTimeOffset.UnixEpoch);

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(gpx, "<ele>").Count);
        Assert.Contains("<ele>12.3</ele>", gpx, StringComparison.Ordinal);
        Assert.Contains("creator=\"RouteTimer\"", gpx, StringComparison.Ordinal);
    }
}
