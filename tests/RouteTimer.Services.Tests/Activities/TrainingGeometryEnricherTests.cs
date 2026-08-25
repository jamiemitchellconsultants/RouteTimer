using RouteTimer.Services.Activities;
using RouteTimer.Services.Routes;
using RouteTimer.Services.Tests.Routes;

namespace RouteTimer.Services.Tests.Activities;

public sealed class TrainingGeometryEnricherTests
{
    [Fact]
    public void Enrich_does_not_derive_gradient_or_curvature_across_gap()
    {
        var activity = ActivityFixtures.CleanedTwoSectionsWithSharpBoundary();

        var enriched = new TrainingGeometryEnricher(RouteProcessingOptions.Default).Enrich(activity);

        var boundary = enriched.Samples.Single(sample => sample.CrossesDiscontinuity);
        Assert.Equal(0, boundary.Gradient, 12);
        Assert.Equal(0, boundary.CurvaturePerMetre, 12);
    }

    [Fact]
    public void Route_and_training_enrichment_share_identical_geometry_values()
    {
        var points = RouteFixtures.PointsExactlyTwentyFiveMetresApart();
        var distances = RouteGeometry.CumulativeDistances(points);
        var expected = RouteGeometry.Enrich(points, distances, 100);

        var actual = new TrainingGeometryEnricher(RouteProcessingOptions.Default).Enrich(ActivityFixtures.CleanedFrom(points)).Samples;

        Assert.Equal(expected.Select(value => value.Gradient), actual.Select(sample => sample.Gradient));
        Assert.Equal(expected.Select(value => value.CurvaturePerMetre), actual.Select(sample => sample.CurvaturePerMetre));
    }

    // Break caught: enrichment overwrites its own raw input, so a second call performs another robust fit.
    [Fact]
    public void Enrich_preserves_raw_elevation_and_is_idempotent_for_nonlinear_profiles()
    {
        var points = ActivityFixtures.NonlinearElevationPoints();
        var enricher = new TrainingGeometryEnricher(RouteProcessingOptions.Default);

        var once = enricher.Enrich(ActivityFixtures.CleanedFrom(points));
        var twice = enricher.Enrich(once);

        Assert.Equal(points.Select(point => point.ElevationMetres), once.Samples.Select(sample => sample.Position.ElevationMetres));
        Assert.Equal(
            once.Samples.Select(sample => (sample.Gradient, sample.CurvaturePerMetre)),
            twice.Samples.Select(sample => (sample.Gradient, sample.CurvaturePerMetre)));
    }
}
