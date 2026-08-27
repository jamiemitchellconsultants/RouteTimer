using RouteTimer.Domain.Models;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Tests.Routes;

public sealed class PredictionGpxWriterTests
{
    private static PredictionGpxSource Source(params PersistedPredictionSegment[] segments) => new(
        "Kingston to Dorking",
        "Predicted 1:12:30 · 34.2 km · 410 m ascent · 28.3 km/h · 214 W · high confidence · model 1.4.0",
        DateTimeOffset.Parse("2026-08-26T09:00:00Z"),
        DateTimeOffset.Parse("2026-08-26T08:00:00Z"),
        segments);

    private static PersistedPredictionSegment Segment(int sequence, double lat, double lon, double ele, double cumulativeSeconds) =>
        new(sequence, lat, lon, ele, 0, 0, 0, 0, 200, 8, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(cumulativeSeconds), ConfidenceLevel.High);

    [Fact]
    public void Writes_an_untimed_course_track()
    {
        var gpx = PredictionGpxWriter.Write(Source(
            Segment(0, 51.4085000, -0.3064000, 12.4, 0),
            Segment(1, 51.4090000, -0.3070000, 15.0, 30)), timed: false);

        Assert.Contains("creator=\"RouteTimer\"", gpx, StringComparison.Ordinal);
        Assert.Contains("<name>Kingston to Dorking</name>", gpx, StringComparison.Ordinal);
        Assert.Contains("<desc>Predicted 1:12:30", gpx, StringComparison.Ordinal);
        Assert.Contains("lat=\"51.4085000\"", gpx, StringComparison.Ordinal);
        Assert.Contains("<ele>12.4</ele>", gpx, StringComparison.Ordinal);
        Assert.DoesNotContain("<trkpt", gpx.Split("<trkseg>")[0], StringComparison.Ordinal);
        Assert.DoesNotContain("<time>2026-08-26T08:00:00Z</time>", gpx, StringComparison.Ordinal);
    }

    [Fact]
    public void Writes_predicted_times_in_the_timed_variant()
    {
        var gpx = PredictionGpxWriter.Write(Source(
            Segment(0, 51.4085000, -0.3064000, 12.4, 0),
            Segment(1, 51.4090000, -0.3070000, 15.0, 90)), timed: true);

        Assert.Contains("<time>2026-08-26T08:00:00.000Z</time>", gpx, StringComparison.Ordinal);
        Assert.Contains("<time>2026-08-26T08:01:30.000Z</time>", gpx, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_not_duplicate_timestamps_for_sub_second_segments()
    {
        var gpx = PredictionGpxWriter.Write(Source(
            Segment(0, 51.4085000, -0.3064000, 12.4, 0),
            Segment(1, 51.4086000, -0.3065000, 12.6, 0.4),
            Segment(2, 51.4087000, -0.3066000, 12.8, 0.8)), timed: true);

        var times = gpx.Split('\n')
            .Where(line => line.Contains("<time>", StringComparison.Ordinal))
            .Skip(1) // metadata/time
            .ToList();

        Assert.Equal(times.Count, times.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Rejects_a_prediction_with_no_segments()
    {
        Assert.Throws<PredictionNotCompleteException>(() => PredictionGpxWriter.Write(Source(), timed: false));
    }

    [Fact]
    public void Writes_no_byte_order_mark()
    {
        var gpx = PredictionGpxWriter.Write(Source(
            Segment(0, 51.4085000, -0.3064000, 12.4, 0),
            Segment(1, 51.4090000, -0.3070000, 15.0, 30)), timed: false);

        Assert.False(gpx.StartsWith('﻿'));
    }

    [Fact]
    public void Slugifies_the_file_name()
    {
        Assert.Equal("Kingston-to-Dorking.gpx", PredictionGpxWriter.SuggestFileName("Kingston to Dorking"));
        Assert.Equal("route.gpx", PredictionGpxWriter.SuggestFileName("///"));
    }
}
