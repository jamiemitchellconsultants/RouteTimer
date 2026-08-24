using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;

namespace RouteTimer.Domain.Tests.Predictions;

public sealed class PredictionResultTests
{
    // Break caught: caller mutations after construction rewrite the prediction that durable publication later observes.
    [Fact]
    public void Constructor_snapshots_segment_and_warning_collections()
    {
        var originalSegment = new PredictionSegment(1, 25, .02, 200, 7, TimeSpan.FromSeconds(4), ConfidenceLevel.High);
        var replacementSegment = originalSegment with { Sequence = 99 };
        var segments = new List<PredictionSegment> { originalSegment };
        var warnings = new List<string> { "power-model-extrapolation" };

        var result = new PredictionResult(segments, TimeSpan.FromSeconds(4), ConfidenceLevel.High, warnings);
        segments[0] = replacementSegment;
        warnings[0] = "conservative-descent-limits";

        Assert.Equal(originalSegment, Assert.Single(result.Segments));
        Assert.Equal("power-model-extrapolation", Assert.Single(result.Warnings));
        Assert.Throws<NotSupportedException>(() => ((IList<PredictionSegment>)result.Segments)[0] = replacementSegment);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Warnings)[0] = "conservative-descent-limits");
    }
}
