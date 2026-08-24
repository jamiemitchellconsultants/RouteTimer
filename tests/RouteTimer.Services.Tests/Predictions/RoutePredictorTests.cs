namespace RouteTimer.Services.Tests.Predictions;

public sealed class RoutePredictorTests
{
    [Fact]
    public void Predict_returns_finite_non_negative_segments_and_total_time()
    {
        var result = PredictionFixtures.PredictStraightRoute();

        Assert.All(result.Segments, segment => Assert.True(double.IsFinite(segment.SpeedMetresPerSecond) && segment.SpeedMetresPerSecond >= 0));
        Assert.True(result.MovingTime > TimeSpan.Zero);
    }
}
