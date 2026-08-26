using RouteTimer.Client.RouteBuilder;
using RouteTimer.Client.RouteBuilder.Models;

namespace RouteTimer.Client.Tests.RouteBuilder;

public class ElevationInterpolationTests
{
    private static List<RoutePoint> Path(int count) =>
        Enumerable.Range(0, count).Select(i => new RoutePoint(51 + i * 0.001, -2)).ToList();

    [Fact]
    public void SampledPointsKeepTheirMeasuredElevation()
    {
        var result = DirectionsInterop.Interpolate(Path(5), [0, 2, 4], [100, 200, 300]);

        Assert.Equal(100, result[0].Elevation);
        Assert.Equal(200, result[2].Elevation);
        Assert.Equal(300, result[4].Elevation);
    }

    [Fact]
    public void PointsBetweenSamplesAreLinearlyInterpolated()
    {
        var result = DirectionsInterop.Interpolate(Path(5), [0, 2, 4], [100, 200, 300]);

        Assert.Equal(150, result[1].Elevation);
        Assert.Equal(250, result[3].Elevation);
    }

    [Fact]
    public void EveryPointGetsAnElevation()
    {
        var result = DirectionsInterop.Interpolate(Path(9), [0, 4, 8], [0, 40, 80]);

        Assert.Equal(9, result.Count);
        Assert.All(result, p => Assert.NotNull(p.Elevation));
    }

    [Fact]
    public void CoordinatesAreNeverMoved()
    {
        var path = Path(5);
        var result = DirectionsInterop.Interpolate(path, [0, 2, 4], [100, 200, 300]);

        Assert.Equal(path.Select(p => p.Lat), result.Select(p => p.Lat));
        Assert.Equal(path.Select(p => p.Lng), result.Select(p => p.Lng));
    }

    [Fact]
    public void TrailingSampleAtTheFinalIndexIsHandled()
    {
        // The caller always appends the last index, which can sit one past the stride.
        var result = DirectionsInterop.Interpolate(Path(4), [0, 2, 3], [10, 30, 40]);

        Assert.Equal(10, result[0].Elevation);
        Assert.Equal(30, result[2].Elevation);
        Assert.Equal(40, result[3].Elevation);
    }

    [Fact]
    public void EveryPointGetsTheSingleSampleElevationWhenOnlyOneSampleExists()
    {
        var result = DirectionsInterop.Interpolate(Path(3), [0], [50]);

        Assert.Equal(50, result[0].Elevation);
        Assert.Equal(50, result[1].Elevation);
        Assert.Equal(50, result[2].Elevation);
    }

    [Fact]
    public void TwoSamplesInterpolateAcrossTheWholePath()
    {
        var result = DirectionsInterop.Interpolate(Path(3), [0, 2], [10, 30]);

        Assert.Equal(10, result[0].Elevation);
        Assert.Equal(20, result[1].Elevation);
        Assert.Equal(30, result[2].Elevation);
    }

    [Fact]
    public void UnevenInteriorStrideInterpolatesEachSegmentIndependently()
    {
        var result = DirectionsInterop.Interpolate(Path(8), [0, 3, 6, 7], [0, 30, 60, 70]);

        Assert.Equal(0, result[0].Elevation);
        Assert.Equal(10, result[1].Elevation);
        Assert.Equal(20, result[2].Elevation);
        Assert.Equal(30, result[3].Elevation);
        Assert.Equal(40, result[4].Elevation);
        Assert.Equal(50, result[5].Elevation);
        Assert.Equal(60, result[6].Elevation);
        Assert.Equal(70, result[7].Elevation);
    }

    [Fact]
    public void Elevation_completeness_is_false_when_any_point_lacks_elevation()
    {
        IReadOnlyList<RoutePoint> complete =
        [
            new RoutePoint(51.5, -0.1, 10),
            new RoutePoint(51.6, -0.2, 20)
        ];
        IReadOnlyList<RoutePoint> partial =
        [
            new RoutePoint(51.5, -0.1, 10),
            new RoutePoint(51.6, -0.2)
        ];

        Assert.True(DirectionsInterop.HasCompleteElevation(complete));
        Assert.False(DirectionsInterop.HasCompleteElevation(partial));
        Assert.False(DirectionsInterop.HasCompleteElevation([]));
    }
}
