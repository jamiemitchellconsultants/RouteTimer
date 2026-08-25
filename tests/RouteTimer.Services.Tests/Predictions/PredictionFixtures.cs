using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Tests.Predictions;

internal static class PredictionFixtures
{
    private static readonly PhysicalCoefficients LosslessCoefficients = new(1, 1, 0, 0);

    public static PredictionResult PredictAcceleratingSegments() => Predict(
        Route((1, 0, 0), (1, 0, 0)),
        Model(new PowerModel([], .5), LosslessCoefficients, calibrated: true),
        new RiderProfile(1, 0));

    public static PredictionResult PredictResistanceDeceleration() => Predict(
        Route((.1, 0, 0)),
        Model(new PowerModel([], 0), new PhysicalCoefficients(1, 1, .005, 0), calibrated: true),
        new RiderProfile(1, 0));

    public static PredictionResult PredictBoundedSubsteps() => Predict(
        Route((2, 0, 0)),
        Model(new PowerModel([], .5), LosslessCoefficients, calibrated: true),
        new RiderProfile(1, 0));

    public static PredictionResult PredictTwoLongSegmentsWithDurationBands()
    {
        var power = new PowerModel(
        [
            Band("-1:1", "0:30", 0, ConfidenceLevel.High),
            Band("-1:1", "30:60", 2, ConfidenceLevel.High),
        ], 0);
        return Predict(Route((1350, 0, 0), (1, 0, 0)), Model(power, LosslessCoefficients, calibrated: true), new RiderProfile(1, 0));
    }

    public static PredictionResult PredictUncoveredCurvedDescent() => Predict(
        Route((1, -.06, .8)),
        Model(ExactPower("-100:-6", .5, ConfidenceLevel.High), LosslessCoefficients, calibrated: true),
        new RiderProfile(1, 0));

    public static PredictionResult PredictFallbackDescentBelowCap() => Predict(
        Route((.01, -.02, 0)),
        Model(ExactPower("-3:-1", 0, ConfidenceLevel.High), LosslessCoefficients, calibrated: true),
        new RiderProfile(1, 0));

    public static PredictionResult PredictWarningOrder() => Predict(
        Route((.01, -.03, 0), (.01, -.03, 0)),
        Model(new PowerModel([], 0), LosslessCoefficients, calibrated: true),
        new RiderProfile(1, 0));

    public static PredictionResult PredictMixedRoute()
    {
        var route = Route((10, 0, 0), (10, .04, .001), (10, -.06, .02), (10, -.01, 0));
        return Predict(route, Model(new PowerModel([], 250), PhysicalCoefficients.Default, calibrated: false), new RiderProfile(75, 10));
    }

    public static PredictionResult PredictSingle(double grade, double watts, double mass, double curvature, double distance) =>
        Predict(Route((distance, grade, curvature)), Model(new PowerModel([], watts), PhysicalCoefficients.Default, calibrated: false), new RiderProfile(mass, 0));

    public static TheoryData<double, double, double, double, double> FinitePropertyCases => new()
    {
        { -.1, 0, 85, 0, 25 },
        { -.03, 100, 65, .02, 5 },
        { 0, 250, 85, 0, 25 },
        { .05, 400, 100, 0, 10 },
    };

    public static PredictionResult PredictWithConfidenceShares(double highShare, double mediumShare, double lowShare, bool calibrated)
    {
        const double mediumGradePowerAtHalfMetrePerSecond = .09804689258202935;
        const double lowGradePowerAtHalfMetrePerSecond = .22042655598364375;
        var power = new PowerModel(
        [
            Band("-1:1", "0:30", 0, ConfidenceLevel.High),
            Band("1:3", "0:30", mediumGradePowerAtHalfMetrePerSecond, ConfidenceLevel.Medium),
            Band("3:6", "0:30", lowGradePowerAtHalfMetrePerSecond, ConfidenceLevel.Low),
        ], 0);
        var segments = new List<(double Distance, double Grade, double Curvature)>();
        if (highShare > 0) segments.Add((highShare * 50, 0, 0));
        if (mediumShare > 0) segments.Add((mediumShare * 50, .02, 0));
        if (lowShare > 0) segments.Add((lowShare * 50, .045, 0));
        return Predict(Route([.. segments]), Model(power, LosslessCoefficients, calibrated), new RiderProfile(1, 0));
    }

    public static PredictionResult PredictZeroPowerDownhill() => Predict(
        Route((1, -.01, 0)),
        Model(new PowerModel([], 0), LosslessCoefficients, calibrated: true),
        new RiderProfile(1, 0));

    public static PredictionResult PredictZeroPowerUphill() => Predict(
        Route((1, .1, 0)),
        Model(new PowerModel([], 0), LosslessCoefficients, calibrated: true),
        new RiderProfile(1, 0));

    public static PredictionResult PredictIterationExhaustion() => Predict(
        Route((65536, 0, 0)),
        Model(new PowerModel([], 0), LosslessCoefficients, calibrated: true),
        new RiderProfile(1, 0));

    public static PredictionResult Predict(
        ProcessedRoute route,
        RiderModel model,
        RiderProfile profile,
        IDescentSpeedLimiter? descentLimiter = null) =>
        new RoutePredictor(descentLimiter ?? new DescentSpeedLimiter()).Predict(route, profile, model);

    public static ProcessedRoute Route(params (double Distance, double Grade, double Curvature)[] segments)
    {
        var samples = new List<RouteSample>
        {
            new(0, new GeoPoint(51, -2, 0), 0, 0, 0, 0),
        };
        var cumulative = 0d;
        var ascent = 0d;
        foreach (var segment in segments)
        {
            cumulative += segment.Distance;
            ascent += Math.Max(0, segment.Grade * segment.Distance);
            samples.Add(new RouteSample(samples.Count, new GeoPoint(51, -2, ascent), cumulative, segment.Distance, segment.Grade, segment.Curvature));
        }

        return new ProcessedRoute(samples, cumulative, ascent);
    }

    public static RiderModel Model(
        PowerModel power,
        PhysicalCoefficients coefficients,
        bool calibrated,
        DescentLimitModel? descents = null) =>
        new(power, coefficients, descents ?? DescentLimitModel.Conservative, calibrated, "test-v1");

    public static PowerModel ExactPower(string gradeKey, double watts, ConfidenceLevel confidence) =>
        new([Band(gradeKey, "0:30", watts, confidence)], watts);

    private static PowerBand Band(string gradeKey, string durationKey, double watts, ConfidenceLevel confidence) =>
        new(gradeKey, durationKey, watts, TimeSpan.FromHours(1), 3, 1, confidence);
}
