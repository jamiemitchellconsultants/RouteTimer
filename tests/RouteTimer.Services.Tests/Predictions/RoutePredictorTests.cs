using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Tests.Predictions;

public sealed class RoutePredictorTests
{
    // Break caught: solving each segment independently discards acceleration and terminal speed.
    [Fact]
    public void Predict_carries_exact_terminal_speed_into_the_next_accelerating_segment()
    {
        var result = PredictionFixtures.PredictAcceleratingSegments();

        Assert.Equal(1, result.Segments[0].SpeedMetresPerSecond, 9);
        Assert.Equal(1.6039127772984754, result.Segments[1].SpeedMetresPerSecond, 9);
        Assert.Equal(1.6234753, result.MovingTime.TotalSeconds, 7);
    }

    // Break caught: retaining equilibrium/minimum speed hides physical deceleration below 0.5 m/s.
    [Fact]
    public void Predict_decelerates_below_the_numerical_force_floor_when_resistance_exceeds_power()
    {
        var segment = Assert.Single(PredictionFixtures.PredictResistanceDeceleration().Segments);

        Assert.Equal(.4950477894383534, segment.SpeedMetresPerSecond, 9);
        Assert.Equal(.2020007, segment.MovingTime.TotalSeconds, 7);
    }

    // Break caught: accepting a proposed step whose elapsed time exceeds one second changes the integration result.
    [Fact]
    public void Predict_halves_and_retries_until_the_final_accepted_substep_is_at_most_one_second()
    {
        var result = PredictionFixtures.PredictBoundedSubsteps();

        Assert.Equal(1.6234753, result.MovingTime.TotalSeconds, 7);
        Assert.Equal(1.231925117678107, Assert.Single(result.Segments).SpeedMetresPerSecond, 9);
    }

    // Break caught: looking up every segment at zero elapsed time misses a crossed duration band.
    [Fact]
    public void Predict_resolves_power_once_at_segment_start_using_cumulative_predicted_time()
    {
        var result = PredictionFixtures.PredictTwoLongSegmentsWithDurationBands();

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(0, result.Segments[0].PowerWatts);
        Assert.Equal(2, result.Segments[1].PowerWatts);
        Assert.Equal(2700, result.Segments[0].MovingTime.TotalSeconds);
        Assert.Equal(result.Segments.Aggregate(TimeSpan.Zero, (sum, segment) => sum + segment.MovingTime), result.MovingTime);
    }

    // Break caught: flooring a runtime curvature limit at 2 m/s overrides a tighter physical cap.
    [Fact]
    public void Predict_applies_actual_curvature_cap_below_two_metres_per_second()
    {
        var result = PredictionFixtures.PredictUncoveredCurvedDescent();

        Assert.Equal(1.0405695161830413, Assert.Single(result.Segments).SpeedMetresPerSecond, 9);
        Assert.Equal(["conservative-descent-limits"], result.Warnings);
        Assert.Equal(ConfidenceLevel.Low, result.Confidence);
    }

    // Break caught: warning only when a fallback cap binds hides missing descent evidence below the cap.
    [Fact]
    public void Predict_marks_every_fallback_descent_even_when_simulated_speed_is_below_the_cap()
    {
        var result = PredictionFixtures.PredictFallbackDescentBelowCap();

        Assert.True(Assert.Single(result.Segments).SpeedMetresPerSecond < 13);
        Assert.Equal(ConfidenceLevel.Low, result.Segments[0].Confidence);
        Assert.Equal(["conservative-descent-limits"], result.Warnings);
    }

    // Break caught: applying limiter confidence to non-descents lowers unrelated segment confidence and adds a false warning.
    [Fact]
    public void Predict_ignores_finite_descent_confidence_for_non_descending_segments()
    {
        const double uphillPowerAtHalfMetrePerSecond = .09804689258202935;
        var route = PredictionFixtures.Route((1, .02, 0));
        var model = PredictionFixtures.Model(
            PredictionFixtures.ExactPower("1:3", uphillPowerAtHalfMetrePerSecond, ConfidenceLevel.High),
            new PhysicalCoefficients(1, 1, 0, 0),
            calibrated: true);
        var limiter = new FixedDescentLimiter(new DescentLimitEstimate(.25, ConfidenceLevel.Low, true));

        var result = PredictionFixtures.Predict(route, model, new RiderProfile(1, 0), limiter);

        Assert.Equal(ConfidenceLevel.High, Assert.Single(result.Segments).Confidence);
        Assert.Empty(result.Warnings);
    }

    // Break caught: set/sort aggregation loses first-seen predictor order or retains repeated warning codes.
    [Fact]
    public void Predict_preserves_first_seen_warning_order_and_deduplicates_ordinally()
    {
        var result = PredictionFixtures.PredictWarningOrder();

        Assert.Equal(["power-model-extrapolation", "conservative-descent-limits"], result.Warnings);
    }

    // Break caught: count-weighted confidence makes short and long segments equally influential.
    [Theory]
    [InlineData(.80, .20, 0, true, ConfidenceLevel.High)]
    [InlineData(.79, .21, 0, true, ConfidenceLevel.Medium)]
    [InlineData(0, .80, .20, true, ConfidenceLevel.Medium)]
    [InlineData(0, .79, .21, true, ConfidenceLevel.Low)]
    [InlineData(.80, .20, 0, false, ConfidenceLevel.Low)]
    public void Predict_uses_exact_time_weighted_confidence_boundaries(
        double highShare,
        double mediumShare,
        double lowShare,
        bool calibrated,
        ConfidenceLevel expected)
    {
        var result = PredictionFixtures.PredictWithConfidenceShares(highShare, mediumShare, lowShare, calibrated);

        Assert.Equal(expected, result.Confidence);
    }

    // Break caught: zero rider power is treated as an automatic stall even when gravity can advance the rider.
    [Fact]
    public void Predict_advances_a_zero_power_downhill()
    {
        var result = PredictionFixtures.PredictZeroPowerDownhill();

        Assert.True(Assert.Single(result.Segments).SpeedMetresPerSecond > .5);
        Assert.True(result.MovingTime > TimeSpan.Zero);
    }

    // Break caught: endlessly halving after a zero-power uphill stall returns partial output or loops forever.
    [Fact]
    public void Predict_rejects_a_zero_power_uphill_that_cannot_make_progress()
    {
        Assert.Throws<PredictionCalculationException>(PredictionFixtures.PredictZeroPowerUphill);
    }

    // Break caught: removing the bounded iteration guard lets impractically slow routes run without convergence.
    [Fact]
    public void Predict_rejects_iteration_exhaustion()
    {
        Assert.Throws<PredictionCalculationException>(PredictionFixtures.PredictIterationExhaustion);
    }

    // Break caught: invalid profile mass enters force calculations and leaks framework exceptions or non-finite state.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Predict_rejects_non_positive_or_non_finite_total_mass(double mass)
    {
        var route = PredictionFixtures.Route((1, 0, 0));
        var model = PredictionFixtures.Model(new PowerModel([], 100), PhysicalCoefficients.Default, calibrated: true);

        Assert.Throws<PredictionCalculationException>(() => PredictionFixtures.Predict(route, model, new RiderProfile(mass, 0)));
    }

    // Break caught: invalid physical coefficients enter force calculations or silently produce non-physical state.
    [Theory]
    [MemberData(nameof(InvalidCoefficientCases))]
    public void Predict_rejects_invalid_physical_coefficients(PhysicalCoefficients coefficients)
    {
        var route = PredictionFixtures.Route((1, 0, 0));
        var model = PredictionFixtures.Model(new PowerModel([], 100), coefficients, calibrated: true);

        Assert.Throws<PredictionCalculationException>(() => PredictionFixtures.Predict(route, model, new RiderProfile(75, 10)));
    }

    public static TheoryData<PhysicalCoefficients> InvalidCoefficientCases => new()
    {
        new PhysicalCoefficients(0, 1.225, .005, .32),
        new PhysicalCoefficients(double.NaN, 1.225, .005, .32),
        new PhysicalCoefficients(.97, 0, .005, .32),
        new PhysicalCoefficients(.97, double.PositiveInfinity, .005, .32),
        new PhysicalCoefficients(.97, 1.225, -.001, .32),
        new PhysicalCoefficients(.97, 1.225, double.NaN, .32),
        new PhysicalCoefficients(.97, 1.225, .005, -.1),
        new PhysicalCoefficients(.97, 1.225, .005, double.PositiveInfinity),
    };

    // Break caught: corrupt route totals or segment geometry bypass the calculation error boundary.
    [Theory]
    [InlineData("total-distance")]
    [InlineData("ascent")]
    [InlineData("segment-distance")]
    [InlineData("gradient")]
    [InlineData("curvature")]
    public void Predict_rejects_non_finite_or_negative_route_values(string kind)
    {
        var valid = PredictionFixtures.Route((1, 0, 0));
        var sample = valid.Samples[1];
        var route = kind switch
        {
            "total-distance" => valid with { DistanceMetres = double.NaN },
            "ascent" => valid with { AscentMetres = -1 },
            "segment-distance" => valid with { Samples = [valid.Samples[0], sample with { SegmentDistanceMetres = 0 }] },
            "gradient" => valid with { Samples = [valid.Samples[0], sample with { Gradient = double.PositiveInfinity }] },
            "curvature" => valid with { Samples = [valid.Samples[0], sample with { CurvaturePerMetre = -1 }] },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var model = PredictionFixtures.Model(new PowerModel([], 100), PhysicalCoefficients.Default, calibrated: true);

        Assert.Throws<PredictionCalculationException>(() => PredictionFixtures.Predict(route, model, new RiderProfile(75, 10)));
    }

    // Break caught: corrupt model power is clamped, propagated, or leaks as a non-calculation exception.
    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Predict_rejects_invalid_power_model_values(double watts)
    {
        var route = PredictionFixtures.Route((1, 0, 0));
        var model = PredictionFixtures.Model(new PowerModel([], watts), PhysicalCoefficients.Default, calibrated: true);

        Assert.Throws<PredictionCalculationException>(() => PredictionFixtures.Predict(route, model, new RiderProfile(75, 10)));
    }

    // Break caught: finite but negative power-model evidence metadata is accepted as a valid model.
    [Fact]
    public void Predict_rejects_negative_power_model_weight()
    {
        var route = PredictionFixtures.Route((1, 0, 0));
        var band = new PowerBand("-1:1", "0:30", 100, TimeSpan.FromMinutes(1), 1, -.1, ConfidenceLevel.High);
        var model = PredictionFixtures.Model(new PowerModel([band], 100), PhysicalCoefficients.Default, calibrated: true);

        Assert.Throws<PredictionCalculationException>(() => PredictionFixtures.Predict(route, model, new RiderProfile(75, 10)));
    }

    // Break caught: overflowing wheel power publishes non-finite kinetic energy instead of a calculation failure.
    [Fact]
    public void Predict_rejects_non_finite_kinetic_energy()
    {
        var route = PredictionFixtures.Route((1, 0, 0));
        var model = PredictionFixtures.Model(new PowerModel([], double.MaxValue), new PhysicalCoefficients(2, 1, 0, 0), calibrated: true);

        Assert.Throws<PredictionCalculationException>(() => PredictionFixtures.Predict(route, model, new RiderProfile(1, 0)));
    }

    // Break caught: stateful proposal sizing or warning aggregation changes identical runs.
    [Fact]
    public void Predict_is_deterministic_and_every_published_value_is_finite_non_negative()
    {
        var first = PredictionFixtures.PredictMixedRoute();
        var second = PredictionFixtures.PredictMixedRoute();

        Assert.Equal(first.Segments, second.Segments);
        Assert.Equal(first.MovingTime, second.MovingTime);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Warnings, second.Warnings);
        Assert.All(first.Segments, segment =>
        {
            Assert.True(double.IsFinite(segment.SpeedMetresPerSecond) && segment.SpeedMetresPerSecond >= 0);
            Assert.True(double.IsFinite(segment.MovingTime.TotalSeconds) && segment.MovingTime > TimeSpan.Zero);
        });
    }

    // Break caught: supported edge inputs yield non-finite speed/time despite completing prediction.
    [Theory]
    [MemberData(nameof(PredictionFixtures.FinitePropertyCases), MemberType = typeof(PredictionFixtures))]
    public void Predict_is_finite_over_supported_inputs(double grade, double watts, double mass, double curvature, double distance)
    {
        var result = PredictionFixtures.PredictSingle(grade, watts, mass, curvature, distance);

        Assert.All(result.Segments, segment => Assert.True(double.IsFinite(segment.SpeedMetresPerSecond) && segment.SpeedMetresPerSecond >= 0));
        Assert.True(result.MovingTime > TimeSpan.Zero);
    }

    private sealed class FixedDescentLimiter(DescentLimitEstimate estimate) : IDescentSpeedLimiter
    {
        public DescentLimitEstimate Resolve(double gradient, double curvaturePerMetre, DescentLimitModel model) => estimate;
    }
}
