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
}
