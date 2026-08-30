using System;
using System.Collections.Generic;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Services.Adjustments;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Tests.Adjustments;

/// <summary>
/// The synthetic route matrix documented in docs/pacing-strategies/backtesting.md, shared by every
/// pacing-strategy test so the documented fixtures have exactly one definition.
/// </summary>
internal static class PacingFixtures
{
    public const string FlatShort = "flat-short";
    public const string FlatLong = "flat-long";
    public const string Rolling = "rolling";
    public const string Mountainous = "mountainous";
    public const string Fractional = "fractional";

    public static IReadOnlyList<string> All => [FlatShort, FlatLong, Rolling, Mountainous, Fractional];

    public static (PacingStrategyContext Context, PredictionResult Baseline) BuildContext(string fixture)
    {
        var predictor = new RoutePredictor(new DescentSpeedLimiter());
        var route = BuildRoute(fixture);
        var profile = new RiderProfile(75, 10);
        var model = new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, DescentLimitModel.Conservative, true, "v1");
        var baseline = predictor.Predict(route, profile, model);
        return (new PacingStrategyContext(Guid.NewGuid(), route, baseline, profile, model), baseline);
    }

    public static PredictionRoute BuildRoute(string fixture)
    {
        var (count, metres, gradient) = fixture switch
        {
            // 12 segments of 50 m at 0%.
            FlatShort => (12, (Func<int, double>)(_ => 50.0), (Func<int, double>)(_ => 0.0)),
            // 120 segments of 100 m alternating -0.5% and +0.5%.
            FlatLong => (120, _ => 100.0, index => index % 2 == 0 ? -0.005 : 0.005),
            // 80 segments repeating -3%, 0%, +3%, +5%.
            Rolling => (80, _ => 80.0, index => (index % 4) switch { 0 => -0.03, 1 => 0.0, 2 => 0.03, _ => 0.05 }),
            // 100 segments of sustained +6%/+9% climb blocks separated by descents.
            Mountainous => (100, _ => 100.0, index => (index % 20) switch
            {
                < 6 => 0.06,
                < 12 => 0.09,
                _ => -0.05,
            }),
            // 31 segments whose durations do not land on whole seconds.
            Fractional => (31, _ => 37.0, _ => 0.0),
            _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture, "Unknown backtesting fixture."),
        };

        var segments = new List<PredictionRouteSegment>(count);
        double cumulativeDistance = 0;
        double elevation = 100;
        double ascent = 0;
        for (int index = 0; index < count; index++)
        {
            double segmentMetres = metres(index);
            double segmentGradient = gradient(index);
            double rise = segmentMetres * segmentGradient;
            cumulativeDistance += segmentMetres;
            elevation += rise;
            if (rise > 0) ascent += rise;

            segments.Add(new PredictionRouteSegment(
                index + 1,
                45.0 + index * 0.0001,
                7.0 + index * 0.0001,
                elevation,
                cumulativeDistance,
                segmentMetres,
                segmentGradient,
                0.0));
        }

        return new PredictionRoute(segments, cumulativeDistance, ascent);
    }
}
