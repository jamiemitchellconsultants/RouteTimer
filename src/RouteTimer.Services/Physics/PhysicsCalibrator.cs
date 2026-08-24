using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;

namespace RouteTimer.Services.Physics;

public sealed class PhysicsCalibrator : IPhysicsCalibrator
{
    private const int MinimumIntervalCount = 60;
    private const double MinimumEvidenceSeconds = 600;
    private const int MinimumActivityCount = 2;
    private const double MinimumSpeedStandardDeviation = 1;
    private const double MinimumGradientRange = .02;
    private const double MinimumCrr = .002;
    private const double MaximumCrr = .012;
    private const double MinimumCdA = .15;
    private const double MaximumCdA = .60;
    private const double MaximumConditionNumber = 1e8;
    private const double MinimumEigenvalue = 1e-12;
    private const double MinimumResidualImprovement = 1e-9;
    private const double MinimumHuberScale = 1e-9;
    private const double HuberThresholdMultiplier = 1.345;
    private const int HuberIterations = 5;

    public PhysicalCalibrationResult Calibrate(
        RiderProfile profile,
        IReadOnlyList<CleanedActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(activities);

        var totalMass = profile.RiderWeightKg + profile.BikeAndEquipmentWeightKg;
        if (!double.IsFinite(totalMass) || totalMass <= 0)
            throw new ArgumentOutOfRangeException(nameof(profile), "Total rider-system mass must be positive and finite.");

        var observations = BuildObservations(activities, totalMass);
        if (!HasMinimumCoverage(observations)) return Fallback("insufficient-physics-evidence");
        if (!HasMinimumSpeedVariation(observations)) return Fallback("ill-conditioned-physics-fit");

        var weights = Enumerable.Repeat(1d, observations.Count).ToArray();
        if (!TrySolve(observations, weights, out var crr, out var cdA))
            return Fallback("ill-conditioned-physics-fit");

        for (var iteration = 0; iteration < HuberIterations; iteration++)
        {
            var residuals = Residuals(observations, crr, cdA);
            var scale = MedianAbsolute(residuals);
            if (!double.IsFinite(scale)) return Fallback("ill-conditioned-physics-fit");
            if (scale <= MinimumHuberScale) break;

            var threshold = HuberThresholdMultiplier * scale;
            for (var index = 0; index < residuals.Length; index++)
            {
                var absoluteResidual = Math.Abs(residuals[index]);
                weights[index] = absoluteResidual <= threshold ? 1 : threshold / absoluteResidual;
            }

            if (!TrySolve(observations, weights, out crr, out cdA))
                return Fallback("ill-conditioned-physics-fit");
        }

        if (!double.IsFinite(crr) || !double.IsFinite(cdA))
            return Fallback("ill-conditioned-physics-fit");

        var fittedObjective = MedianAbsolute(Residuals(observations, crr, cdA));
        var defaultObjective = MedianAbsolute(Residuals(
            observations,
            PhysicalCoefficients.Default.Crr,
            PhysicalCoefficients.Default.CdA));
        var improvement = defaultObjective - fittedObjective;
        if (!double.IsFinite(fittedObjective)
            || !double.IsFinite(defaultObjective)
            || !double.IsFinite(improvement)
            || improvement < MinimumResidualImprovement)
        {
            return Fallback("physics-fit-not-improved");
        }

        return new PhysicalCalibrationResult(
            new PhysicalCoefficients(
                PhysicalCoefficients.Default.DrivetrainEfficiency,
                PhysicalCoefficients.Default.AirDensity,
                crr,
                cdA),
            true,
            "physics-calibrated");
    }

    private static List<Observation> BuildObservations(
        IReadOnlyList<CleanedActivity> activities,
        double totalMass)
    {
        var observations = new List<Observation>();
        for (var activityIndex = 0; activityIndex < activities.Count; activityIndex++)
        {
            var activity = activities[activityIndex];
            if (activity.Quality.Eligibility != ActivityEligibility.Eligible) continue;

            for (var sampleIndex = 1; sampleIndex < activity.Samples.Count; sampleIndex++)
            {
                var start = activity.Samples[sampleIndex - 1];
                var end = activity.Samples[sampleIndex];
                if (!TryCreateObservation(activity.Name, activityIndex, start, end, totalMass, out var observation))
                    continue;
                observations.Add(observation);
            }
        }

        observations.Sort(ObservationComparer.Instance);
        return observations;
    }

    private static bool TryCreateObservation(
        string activityName,
        int activityIndex,
        CleanRideSample start,
        CleanRideSample end,
        double totalMass,
        out Observation observation)
    {
        observation = default;
        if (end.CrossesDiscontinuity
            || start.PowerWatts is null
            || end.PowerWatts is null
            || !IsFinite(start)
            || !IsFinite(end))
        {
            return false;
        }

        var seconds = (end.Timestamp - start.Timestamp).TotalSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 10) return false;

        var speed = (start.SpeedMetresPerSecond + end.SpeedMetresPerSecond) / 2;
        var grade = (start.Gradient + end.Gradient) / 2;
        var power = (start.PowerWatts.Value + end.PowerWatts.Value) / 2d;
        var acceleration = (end.SpeedMetresPerSecond - start.SpeedMetresPerSecond) / seconds;
        if (!double.IsFinite(speed)
            || !double.IsFinite(grade)
            || !double.IsFinite(power)
            || !double.IsFinite(acceleration)
            || speed < 3
            || speed > 20
            || power < 1
            || power > 2000
            || grade < -.02
            || grade > .20
            || Math.Abs(acceleration) > .30)
        {
            return false;
        }

        var wheelPower = power * PhysicalCoefficients.Default.DrivetrainEfficiency;
        var response = wheelPower / speed
            - CyclingForces.GravityForce(grade, totalMass)
            - totalMass * acceleration;
        var rollingBasis = totalMass
            * CyclingForces.GravityMetresPerSecondSquared
            * Math.Cos(Math.Atan(grade));
        var aerodynamicBasis = .5
            * PhysicalCoefficients.Default.AirDensity
            * speed
            * speed;
        if (!double.IsFinite(response)
            || !double.IsFinite(rollingBasis)
            || !double.IsFinite(aerodynamicBasis))
        {
            return false;
        }

        observation = new Observation(
            activityName,
            activityIndex,
            start.Timestamp,
            end.Timestamp,
            seconds,
            speed,
            grade,
            response,
            rollingBasis,
            aerodynamicBasis);
        return true;
    }

    private static bool IsFinite(CleanRideSample sample) =>
        double.IsFinite(sample.SpeedMetresPerSecond)
        && double.IsFinite(sample.Gradient)
        && double.IsFinite(sample.Position.Latitude)
        && double.IsFinite(sample.Position.Longitude)
        && double.IsFinite(sample.Position.ElevationMetres);

    private static bool HasMinimumCoverage(IReadOnlyList<Observation> observations)
    {
        if (observations.Count < MinimumIntervalCount
            || observations.Sum(observation => observation.Seconds) < MinimumEvidenceSeconds
            || observations.Select(observation => observation.ActivityIndex).Distinct().Count() < MinimumActivityCount)
        {
            return false;
        }

        var minimumGrade = observations.Min(observation => observation.Grade);
        var maximumGrade = observations.Max(observation => observation.Grade);
        return double.IsFinite(minimumGrade)
            && double.IsFinite(maximumGrade)
            && maximumGrade - minimumGrade >= MinimumGradientRange;
    }

    private static bool HasMinimumSpeedVariation(IReadOnlyList<Observation> observations)
    {
        var meanSpeed = observations.Average(observation => observation.Speed);
        var speedVariance = observations.Average(observation =>
        {
            var difference = observation.Speed - meanSpeed;
            return difference * difference;
        });
        var speedStandardDeviation = Math.Sqrt(speedVariance);
        return double.IsFinite(speedStandardDeviation)
            && speedStandardDeviation >= MinimumSpeedStandardDeviation;
    }

    private static bool TrySolve(
        IReadOnlyList<Observation> observations,
        IReadOnlyList<double> weights,
        out double crr,
        out double cdA)
    {
        var rollingSquared = 0d;
        var crossProduct = 0d;
        var aerodynamicSquared = 0d;
        var rollingResponse = 0d;
        var aerodynamicResponse = 0d;
        for (var index = 0; index < observations.Count; index++)
        {
            var observation = observations[index];
            var weight = weights[index];
            rollingSquared += weight * observation.RollingBasis * observation.RollingBasis;
            crossProduct += weight * observation.RollingBasis * observation.AerodynamicBasis;
            aerodynamicSquared += weight * observation.AerodynamicBasis * observation.AerodynamicBasis;
            rollingResponse += weight * observation.RollingBasis * observation.Response;
            aerodynamicResponse += weight * observation.AerodynamicBasis * observation.Response;
        }

        if (!double.IsFinite(rollingSquared)
            || !double.IsFinite(crossProduct)
            || !double.IsFinite(aerodynamicSquared)
            || !double.IsFinite(rollingResponse)
            || !double.IsFinite(aerodynamicResponse))
        {
            crr = default;
            cdA = default;
            return false;
        }

        var trace = rollingSquared + aerodynamicSquared;
        var discriminant = Math.Sqrt(
            (rollingSquared - aerodynamicSquared) * (rollingSquared - aerodynamicSquared)
            + 4 * crossProduct * crossProduct);
        var maximumEigenvalue = (trace + discriminant) / 2;
        var minimumEigenvalue = (trace - discriminant) / 2;
        if (!double.IsFinite(trace)
            || !double.IsFinite(discriminant)
            || !double.IsFinite(minimumEigenvalue)
            || !double.IsFinite(maximumEigenvalue)
            || minimumEigenvalue <= MinimumEigenvalue
            || maximumEigenvalue / minimumEigenvalue > MaximumConditionNumber)
        {
            crr = default;
            cdA = default;
            return false;
        }

        var determinant = rollingSquared * aerodynamicSquared - crossProduct * crossProduct;
        var fittedCrr = (rollingResponse * aerodynamicSquared - aerodynamicResponse * crossProduct) / determinant;
        var fittedCdA = (rollingSquared * aerodynamicResponse - crossProduct * rollingResponse) / determinant;
        if (!double.IsFinite(fittedCrr) || !double.IsFinite(fittedCdA))
        {
            crr = default;
            cdA = default;
            return false;
        }

        crr = Math.Clamp(fittedCrr, MinimumCrr, MaximumCrr);
        cdA = Math.Clamp(fittedCdA, MinimumCdA, MaximumCdA);
        return true;
    }

    private static double[] Residuals(
        IReadOnlyList<Observation> observations,
        double crr,
        double cdA) =>
        observations.Select(observation =>
                observation.Response
                - observation.RollingBasis * crr
                - observation.AerodynamicBasis * cdA)
            .ToArray();

    private static double MedianAbsolute(IEnumerable<double> values)
    {
        var ordered = values.Select(Math.Abs).Order().ToArray();
        if (ordered.Length == 0) return double.NaN;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static PhysicalCalibrationResult Fallback(string reasonCode) =>
        new(PhysicalCoefficients.Default, false, reasonCode);

    private readonly record struct Observation(
        string ActivityName,
        int ActivityIndex,
        DateTimeOffset Start,
        DateTimeOffset End,
        double Seconds,
        double Speed,
        double Grade,
        double Response,
        double RollingBasis,
        double AerodynamicBasis);

    private sealed class ObservationComparer : IComparer<Observation>
    {
        public static ObservationComparer Instance { get; } = new();

        public int Compare(Observation left, Observation right)
        {
            var comparison = string.Compare(left.ActivityName, right.ActivityName, StringComparison.Ordinal);
            if (comparison != 0) return comparison;
            comparison = left.Start.CompareTo(right.Start);
            if (comparison != 0) return comparison;
            comparison = left.End.CompareTo(right.End);
            if (comparison != 0) return comparison;
            comparison = CompareDouble(left.Seconds, right.Seconds);
            if (comparison != 0) return comparison;
            comparison = CompareDouble(left.Speed, right.Speed);
            if (comparison != 0) return comparison;
            comparison = CompareDouble(left.Grade, right.Grade);
            if (comparison != 0) return comparison;
            comparison = CompareDouble(left.Response, right.Response);
            if (comparison != 0) return comparison;
            comparison = CompareDouble(left.RollingBasis, right.RollingBasis);
            return comparison != 0
                ? comparison
                : CompareDouble(left.AerodynamicBasis, right.AerodynamicBasis);
        }

        private static int CompareDouble(double left, double right)
        {
            var comparison = left.CompareTo(right);
            return comparison != 0
                ? comparison
                : BitConverter.DoubleToInt64Bits(left).CompareTo(BitConverter.DoubleToInt64Bits(right));
        }
    }
}
