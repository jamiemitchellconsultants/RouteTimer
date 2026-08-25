using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Physics;

namespace RouteTimer.Services.Tests.Physics;

public sealed class PhysicsCalibratorTests
{
    // Break caught: fitting ordinary least squares, depending on caller order, or using the wrong force balance
    // prevents robust deterministic recovery of the two physical coefficients.
    [Fact]
    public void Calibrate_recovers_known_coefficients_with_outliers_independent_of_activity_order()
    {
        var expected = new PhysicalCoefficients(.97, 1.225, .006, .32);
        var activities = PhysicsFixtures.SyntheticActivities(expected, 3, 8, includeOutliers: true);

        var forward = new PhysicsCalibrator().Calibrate(PhysicsFixtures.Profile, activities);
        var reverse = new PhysicsCalibrator().Calibrate(PhysicsFixtures.Profile, activities.Reverse().ToArray());

        Assert.True(forward.WasCalibrated);
        Assert.Equal("physics-calibrated", forward.ReasonCode);
        Assert.InRange(forward.Coefficients.Crr, expected.Crr - .0005, expected.Crr + .0005);
        Assert.InRange(forward.Coefficients.CdA, expected.CdA - .02, expected.CdA + .02);
        Assert.Equal(forward, reverse);
    }

    // Break caught: treating duplicate activity-name/timestamp keys as equal leaves List.Sort free to accumulate
    // differing observations in caller order, producing bitwise-different fitted coefficients after reversal.
    [Fact]
    public void Calibrate_is_order_independent_when_primary_sort_keys_collide()
    {
        var activities = PhysicsFixtures.CollidingPrimaryKeyActivities();

        var forward = new PhysicsCalibrator().Calibrate(PhysicsFixtures.Profile, activities);
        var reverse = new PhysicsCalibrator().Calibrate(PhysicsFixtures.Profile, activities.Reverse().ToArray());

        Assert.True(forward.WasCalibrated);
        Assert.Equal(forward, reverse);
    }

    // Break caught: an unconstrained solve can publish physically implausible coefficients.
    [Theory]
    [InlineData(.001, .10, .002, .15)]
    [InlineData(.020, .80, .012, .60)]
    public void Calibrate_clamps_every_accepted_coefficient_to_physical_bounds(
        double sourceCrr,
        double sourceCdA,
        double expectedCrr,
        double expectedCdA)
    {
        var source = new PhysicalCoefficients(.97, 1.225, sourceCrr, sourceCdA);

        var result = new PhysicsCalibrator().Calibrate(
            PhysicsFixtures.Profile,
            PhysicsFixtures.SyntheticActivities(source, 3, 8));

        Assert.True(result.WasCalibrated);
        Assert.Equal(expectedCrr, result.Coefficients.Crr, 12);
        Assert.Equal(expectedCdA, result.Coefficients.CdA, 12);
        Assert.Equal(.97, result.Coefficients.DrivetrainEfficiency);
        Assert.Equal(1.225, result.Coefficients.AirDensity);
    }

    // Break caught: expected evidence failures can leak unstable diagnostics or unsafe fitted values.
    [Theory]
    [InlineData("too-few", "insufficient-physics-evidence")]
    [InlineData("single-speed", "ill-conditioned-physics-fit")]
    [InlineData("worse-than-default", "physics-fit-not-improved")]
    public void Calibrate_returns_stable_default_fallback(string evidence, string reason)
    {
        var result = new PhysicsCalibrator().Calibrate(PhysicsFixtures.Profile, PhysicsFixtures.Named(evidence));

        Assert.Equal(new PhysicalCalibrationResult(PhysicalCoefficients.Default, false, reason), result);
    }

    // Break caught: relaxing any evidence filter lets a single invalid interval satisfy the minimum coverage gate.
    [Theory]
    [InlineData("ineligible-activity")]
    [InlineData("discontinuity")]
    [InlineData("missing-start-power")]
    [InlineData("missing-end-power")]
    [InlineData("non-finite-start-speed")]
    [InlineData("non-finite-end-speed")]
    [InlineData("non-finite-start-gradient")]
    [InlineData("non-finite-end-gradient")]
    [InlineData("non-finite-latitude")]
    [InlineData("non-finite-longitude")]
    [InlineData("non-finite-elevation")]
    [InlineData("zero-duration")]
    [InlineData("overlong-duration")]
    [InlineData("speed-below-range")]
    [InlineData("speed-above-range")]
    [InlineData("power-below-range")]
    [InlineData("power-above-range")]
    [InlineData("grade-below-range")]
    [InlineData("grade-above-range")]
    [InlineData("acceleration-above-range")]
    public void Calibrate_excludes_intervals_that_fail_a_global_constraint(string invalidity)
    {
        var activities = PhysicsFixtures.ExactlyMinimumActivitiesWithOneInvalidInterval(invalidity);

        var result = new PhysicsCalibrator().Calibrate(PhysicsFixtures.Profile, activities);

        Assert.Equal(
            new PhysicalCalibrationResult(PhysicalCoefficients.Default, false, "insufficient-physics-evidence"),
            result);
    }

    // Break caught: implementing exclusive comparisons rejects valid observations at documented endpoints.
    [Fact]
    public void Calibrate_accepts_inclusive_speed_and_gradient_boundaries()
    {
        var activities = PhysicsFixtures.BoundaryActivities();

        var result = new PhysicsCalibrator().Calibrate(PhysicsFixtures.Profile, activities);

        Assert.True(result.WasCalibrated);
        Assert.Equal("physics-calibrated", result.ReasonCode);
    }
}

internal static class PhysicsFixtures
{
    private const double Gravity = 9.80665;
    private const double TotalMass = 85;
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    public static RiderProfile Profile { get; } = new(75, 10);

    public static IReadOnlyList<CleanedActivity> SyntheticActivities(
        PhysicalCoefficients coefficients,
        int activityCount,
        int minutesEach,
        bool includeOutliers = false)
    {
        var intervalsPerActivity = minutesEach * 6;
        return Enumerable.Range(0, activityCount)
            .Select(activityIndex => Activity(
                $"Ride-{activityIndex:D2}",
                activityIndex,
                intervalsPerActivity,
                intervalIndex => SyntheticInterval(coefficients, intervalIndex, includeOutliers)))
            .ToArray();
    }

    public static IReadOnlyList<CleanedActivity> Named(string evidence) => evidence switch
    {
        "too-few" => Enumerable.Range(0, 2)
            .Select(index => Activity($"Short-{index}", index, 29,
                interval => SyntheticInterval(new PhysicalCoefficients(.97, 1.225, .006, .32), interval, false)))
            .ToArray(),
        "single-speed" => Enumerable.Range(0, 2)
            .Select(index => Activity($"Constant-{index}", index, 30,
                interval => SyntheticInterval(
                    new PhysicalCoefficients(.97, 1.225, .006, .32),
                    interval,
                    false,
                    speedOverride: 8)))
            .ToArray(),
        "worse-than-default" => Enumerable.Range(0, 2)
            .Select(index => Activity($"Default-{index}", index, 30,
                interval => ExactDefaultInterval(interval)))
            .ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(evidence))
    };

    public static IReadOnlyList<CleanedActivity> ExactlyMinimumActivitiesWithOneInvalidInterval(string invalidity)
    {
        var activities = Enumerable.Range(0, 2)
            .Select(index => Activity($"Minimum-{index}", index, 30,
                interval => SyntheticInterval(new PhysicalCoefficients(.97, 1.225, .006, .32), interval, false)))
            .ToArray();

        if (invalidity == "ineligible-activity")
        {
            activities[0] = activities[0] with
            {
                Quality = activities[0].Quality with { Eligibility = ActivityEligibility.Ineligible }
            };
            return activities;
        }

        var samples = activities[0].Samples.ToArray();
        var start = samples[0];
        var end = samples[1];
        switch (invalidity)
        {
            case "discontinuity": end = end with { CrossesDiscontinuity = true }; break;
            case "missing-start-power": start = start with { PowerWatts = null }; break;
            case "missing-end-power": end = end with { PowerWatts = null }; break;
            case "non-finite-start-speed": start = start with { SpeedMetresPerSecond = double.NaN }; break;
            case "non-finite-end-speed": end = end with { SpeedMetresPerSecond = double.PositiveInfinity }; break;
            case "non-finite-start-gradient": start = start with { Gradient = double.NaN }; break;
            case "non-finite-end-gradient": end = end with { Gradient = double.NegativeInfinity }; break;
            case "non-finite-latitude": start = start with { Position = start.Position with { Latitude = double.NaN } }; break;
            case "non-finite-longitude": start = start with { Position = start.Position with { Longitude = double.PositiveInfinity } }; break;
            case "non-finite-elevation": start = start with { Position = start.Position with { ElevationMetres = double.NaN } }; break;
            case "zero-duration": end = end with { Timestamp = start.Timestamp }; break;
            case "overlong-duration": end = end with { Timestamp = start.Timestamp.AddSeconds(10.001) }; break;
            case "speed-below-range": start = start with { SpeedMetresPerSecond = 2.99 }; end = end with { SpeedMetresPerSecond = 2.99 }; break;
            case "speed-above-range": start = start with { SpeedMetresPerSecond = 20.01 }; end = end with { SpeedMetresPerSecond = 20.01 }; break;
            case "power-below-range": start = start with { PowerWatts = 0 }; end = end with { PowerWatts = 0 }; break;
            case "power-above-range": start = start with { PowerWatts = 2001 }; end = end with { PowerWatts = 2001 }; break;
            case "grade-below-range": start = start with { Gradient = -.0201 }; end = end with { Gradient = -.0201 }; break;
            case "grade-above-range": start = start with { Gradient = .2001 }; end = end with { Gradient = .2001 }; break;
            case "acceleration-above-range": start = start with { SpeedMetresPerSecond = 5 }; end = end with { SpeedMetresPerSecond = 8.01 }; break;
            default: throw new ArgumentOutOfRangeException(nameof(invalidity));
        }

        samples[0] = start;
        samples[1] = end;
        activities[0] = activities[0] with { Samples = samples };
        return activities;
    }

    public static IReadOnlyList<CleanedActivity> BoundaryActivities()
    {
        var expected = new PhysicalCoefficients(.97, 1.225, .006, .32);
        return Enumerable.Range(0, 2)
            .Select(index => Activity($"Boundary-{index}", index, 30, interval =>
            {
                var speed = interval % 2 == 0 ? 3 : 20;
                var grade = interval % 2 == 0 ? .20 : -.02;
                return FromPhysicalBalance(expected, speed, grade, 0, false);
            }))
            .ToArray();
    }

    public static IReadOnlyList<CleanedActivity> CollidingPrimaryKeyActivities()
    {
        var expected = new PhysicalCoefficients(.97, 1.225, .006, .35);
        return Enumerable.Range(0, 12)
            .Select(variant => Activity(
                "Same ride name",
                0,
                48,
                interval => SyntheticInterval(expected, interval + variant * 11, variant % 4 == 0)))
            .ToArray();
    }

    private static CleanedActivity Activity(
        string name,
        int activityIndex,
        int intervalCount,
        Func<int, IntervalValues> makeInterval)
    {
        var samples = new List<CleanRideSample>(intervalCount * 2);
        var timestamp = Epoch.AddDays(activityIndex);
        for (var intervalIndex = 0; intervalIndex < intervalCount; intervalIndex++)
        {
            var interval = makeInterval(intervalIndex + activityIndex * intervalCount);
            var first = Sample(timestamp, interval.Speed0, interval.Power, interval.Grade, samples.Count > 0, intervalIndex);
            var second = Sample(timestamp.AddSeconds(10), interval.Speed1, interval.Power, interval.Grade, false, intervalIndex);
            samples.Add(first);
            samples.Add(second);
            timestamp = timestamp.AddSeconds(20);
        }

        return new CleanedActivity(
            name,
            samples,
            TimeSpan.FromSeconds(intervalCount * 10),
            new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []));
    }

    private static IntervalValues SyntheticInterval(
        PhysicalCoefficients coefficients,
        int intervalIndex,
        bool includeOutliers,
        double? speedOverride = null)
    {
        var midpointSpeed = speedOverride ?? 4 + intervalIndex % 7 * 1.4;
        var grade = intervalIndex % 5 * .015;
        var acceleration = speedOverride.HasValue ? 0 : (intervalIndex % 3 - 1) * .05;
        return FromPhysicalBalance(coefficients, midpointSpeed, grade, acceleration,
            includeOutliers && intervalIndex % 23 == 0);
    }

    private static IntervalValues ExactDefaultInterval(int intervalIndex)
    {
        var grade = intervalIndex % 5 * .015;
        var approximateSpeed = 4 + intervalIndex % 7 * 1.4;
        var approximatePower = RiderPower(PhysicalCoefficients.Default, approximateSpeed, grade, 0);
        var integerPower = (ushort)Math.Clamp(Math.Round(approximatePower), 1, 2000);
        var exactSpeed = SolveSpeedForPower(PhysicalCoefficients.Default, grade, integerPower);
        return new IntervalValues(exactSpeed, exactSpeed, integerPower, grade);
    }

    private static IntervalValues FromPhysicalBalance(
        PhysicalCoefficients coefficients,
        double midpointSpeed,
        double grade,
        double acceleration,
        bool outlier)
    {
        var riderPower = RiderPower(coefficients, midpointSpeed, grade, acceleration);
        if (outlier) riderPower += 250;
        var power = (ushort)Math.Clamp(Math.Round(riderPower), 1, 2000);
        return new IntervalValues(
            midpointSpeed - acceleration * 5,
            midpointSpeed + acceleration * 5,
            power,
            grade);
    }

    // This test-only equation is intentionally independent of CyclingForces and PhysicsCalibrator.
    private static double RiderPower(
        PhysicalCoefficients coefficients,
        double speed,
        double grade,
        double acceleration)
    {
        var incline = Math.Atan(grade);
        var gravityForce = TotalMass * Gravity * Math.Sin(incline);
        var rollingForce = TotalMass * Gravity * Math.Cos(incline) * coefficients.Crr;
        var aerodynamicForce = .5 * coefficients.AirDensity * coefficients.CdA * speed * speed;
        return (gravityForce + rollingForce + aerodynamicForce + TotalMass * acceleration)
            * speed / coefficients.DrivetrainEfficiency;
    }

    private static double SolveSpeedForPower(PhysicalCoefficients coefficients, double grade, ushort power)
    {
        var low = 3d;
        var high = 20d;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var middle = (low + high) / 2;
            if (RiderPower(coefficients, middle, grade, 0) < power) low = middle;
            else high = middle;
        }

        return (low + high) / 2;
    }

    private static CleanRideSample Sample(
        DateTimeOffset timestamp,
        double speed,
        ushort power,
        double grade,
        bool crossesDiscontinuity,
        int intervalIndex) =>
        new(
            timestamp,
            timestamp - Epoch,
            new GeoPoint(51 + intervalIndex * .00001, -2, 100),
            speed,
            power,
            140,
            85,
            crossesDiscontinuity,
            grade,
            0);

    private sealed record IntervalValues(double Speed0, double Speed1, ushort Power, double Grade);
}
