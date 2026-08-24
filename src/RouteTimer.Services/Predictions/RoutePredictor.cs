using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Models;
using RouteTimer.Services.Physics;

namespace RouteTimer.Services.Predictions;

public sealed class RoutePredictor : IRoutePredictor
{
    private const double InitialSpeedMetresPerSecond = .5;
    private const double MaximumSubstepSeconds = 1;
    private const int MaximumIterationsPerSegment = 100_000;
    private readonly IDescentSpeedLimiter _descentLimiter;

    public RoutePredictor(IDescentSpeedLimiter descentLimiter) =>
        _descentLimiter = descentLimiter ?? throw new ArgumentNullException(nameof(descentLimiter));

    public PredictionResult Predict(ProcessedRoute route, RiderProfile profile, RiderModel model)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(model);
        ValidateInputs(route, profile, model);

        var lookup = new PowerLookup(model.PowerModel);
        var segments = new List<PredictionSegment>(route.Samples.Count - 1);
        var warnings = new List<string>();
        var warningSet = new HashSet<string>(StringComparer.Ordinal);
        var elapsed = TimeSpan.Zero;
        var mass = profile.RiderWeightKg + profile.BikeAndEquipmentWeightKg;
        var entrySpeed = InitialSpeedMetresPerSecond;
        var physicalConfidence = model.WasCalibrated ? ConfidenceLevel.High : ConfidenceLevel.Low;

        foreach (var sample in route.Samples.Skip(1))
        {
            var estimate = lookup.GetWatts(sample.Gradient, elapsed);
            if (!double.IsFinite(estimate.Watts) || estimate.Watts < 0 || !Enum.IsDefined(estimate.Confidence))
                throw new PredictionCalculationException("Prediction resolved invalid rider power.");

            var descent = _descentLimiter.Resolve(sample.Gradient, sample.CurvaturePerMetre, model.DescentLimits);
            ValidateDescent(descent);
            var hasDescentCap = sample.Gradient < 0 && double.IsFinite(descent.SpeedCapMetresPerSecond);
            var segmentConfidence = Min(estimate.Confidence, physicalConfidence);
            if (hasDescentCap) segmentConfidence = Min(segmentConfidence, descent.Confidence);
            if (estimate.Extrapolated) AddWarning(PredictionWarningCodes.PowerModelExtrapolation, warnings, warningSet);
            if (hasDescentCap && descent.UsedFallback) AddWarning(PredictionWarningCodes.ConservativeDescentLimits, warnings, warningSet);

            var remainingDistance = sample.SegmentDistanceMetres;
            var proposal = remainingDistance;
            var segmentSeconds = 0d;
            var iterations = 0;

            while (remainingDistance > 0)
            {
                if (++iterations > MaximumIterationsPerSegment)
                    throw new PredictionCalculationException("Prediction exceeded the segment iteration limit.");
                if (!double.IsFinite(proposal) || proposal <= 0 || proposal > remainingDistance)
                    throw new PredictionCalculationException("Prediction could not make progress along the route.");

                var advanced = TryAdvance(entrySpeed, proposal, sample.Gradient, estimate.Watts, mass, model.Coefficients);
                if (advanced is null)
                {
                    proposal = HalveProposal(proposal);
                    continue;
                }

                var exitSpeed = hasDescentCap
                    ? Math.Min(advanced.ExitSpeedMetresPerSecond, descent.SpeedCapMetresPerSecond)
                    : advanced.ExitSpeedMetresPerSecond;
                var seconds = hasDescentCap
                    ? 2 * proposal / (entrySpeed + exitSpeed)
                    : advanced.Seconds;
                if (!double.IsFinite(exitSpeed) || exitSpeed < 0 || !double.IsFinite(seconds) || seconds <= 0)
                    throw new PredictionCalculationException("Prediction produced invalid speed or time.");
                if (seconds > MaximumSubstepSeconds)
                {
                    proposal = HalveProposal(proposal);
                    continue;
                }

                var nextRemaining = remainingDistance - proposal;
                if (!double.IsFinite(nextRemaining) || nextRemaining < 0 || nextRemaining >= remainingDistance)
                    throw new PredictionCalculationException("Prediction could not make progress along the route.");
                segmentSeconds += seconds;
                if (!double.IsFinite(segmentSeconds) || segmentSeconds <= 0)
                    throw new PredictionCalculationException("Prediction produced invalid segment time.");

                remainingDistance = nextRemaining;
                entrySpeed = exitSpeed;
                proposal = Math.Min(proposal, remainingDistance);
            }

            var duration = ToDuration(segmentSeconds);
            var speed = sample.SegmentDistanceMetres / duration.TotalSeconds;
            if (!double.IsFinite(speed) || speed < 0)
                throw new PredictionCalculationException("Prediction produced invalid segment speed.");
            try
            {
                elapsed += duration;
            }
            catch (OverflowException)
            {
                throw new PredictionCalculationException("Prediction produced invalid cumulative time.");
            }

            if (elapsed < TimeSpan.Zero)
                throw new PredictionCalculationException("Prediction produced invalid cumulative time.");
            segments.Add(new PredictionSegment(
                sample.Sequence,
                sample.SegmentDistanceMetres,
                sample.Gradient,
                estimate.Watts,
                speed,
                duration,
                segmentConfidence));
        }

        var confidence = RouteConfidence(segments, model.WasCalibrated);
        return new PredictionResult(segments, elapsed, confidence, warnings.AsReadOnly());
    }

    private static AcceptedSubstep? TryAdvance(
        double entrySpeed,
        double distance,
        double grade,
        double riderPower,
        double mass,
        PhysicalCoefficients coefficients)
    {
        var forceSpeed = Math.Max(entrySpeed, InitialSpeedMetresPerSecond);
        var wheelPower = riderPower * coefficients.DrivetrainEfficiency;
        var drivingForce = wheelPower / forceSpeed;
        double resistance;
        try
        {
            resistance = CyclingForces.GravityForce(grade, mass)
                + CyclingForces.RollingForce(grade, mass, coefficients.Crr)
                + CyclingForces.AerodynamicForce(entrySpeed, coefficients.AirDensity, coefficients.CdA);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new PredictionCalculationException("Prediction produced invalid resistance force.");
        }

        var acceleration = (drivingForce - resistance) / mass;
        var exitSquared = entrySpeed * entrySpeed + 2 * acceleration * distance;
        if (!double.IsFinite(exitSquared))
            throw new PredictionCalculationException("Prediction produced non-finite energy.");
        if (exitSquared < 0) return null;

        var exitSpeed = Math.Sqrt(exitSquared);
        var seconds = 2 * distance / (entrySpeed + exitSpeed);
        if (!double.IsFinite(exitSpeed) || exitSpeed < 0 || !double.IsFinite(seconds) || seconds <= 0)
            throw new PredictionCalculationException("Prediction could not advance along the route.");
        return new AcceptedSubstep(exitSpeed, seconds);
    }

    private static void ValidateInputs(ProcessedRoute route, RiderProfile profile, RiderModel model)
    {
        var mass = profile.RiderWeightKg + profile.BikeAndEquipmentWeightKg;
        if (!double.IsFinite(mass) || mass <= 0)
            throw new PredictionCalculationException("Prediction requires positive finite total mass.");
        if (!double.IsFinite(route.DistanceMetres) || route.DistanceMetres < 0 ||
            !double.IsFinite(route.AscentMetres) || route.AscentMetres < 0 ||
            route.Samples is null || route.Samples.Count < 2)
            throw new PredictionCalculationException("Prediction route geometry is invalid.");

        foreach (var sample in route.Samples)
        {
            if (sample is null ||
                !double.IsFinite(sample.Point.Latitude) ||
                !double.IsFinite(sample.Point.Longitude) ||
                !double.IsFinite(sample.Point.ElevationMetres) ||
                !double.IsFinite(sample.CumulativeDistanceMetres) || sample.CumulativeDistanceMetres < 0 ||
                !double.IsFinite(sample.SegmentDistanceMetres) || sample.SegmentDistanceMetres < 0 ||
                !double.IsFinite(sample.Gradient) ||
                !double.IsFinite(sample.CurvaturePerMetre) || sample.CurvaturePerMetre < 0)
                throw new PredictionCalculationException("Prediction route geometry is invalid.");
        }

        if (route.Samples.Skip(1).Any(sample => sample.SegmentDistanceMetres <= 0))
            throw new PredictionCalculationException("Prediction segments require positive finite distance.");
        if (model.PowerModel is null || model.PowerModel.Bands is null ||
            !double.IsFinite(model.PowerModel.GlobalTypicalWatts) || model.PowerModel.GlobalTypicalWatts < 0)
            throw new PredictionCalculationException("Prediction power model is invalid.");
        if (model.PowerModel.Bands.Any(band => band is null ||
                !double.IsFinite(band.TypicalWatts) || band.TypicalWatts < 0 ||
                band.Evidence < TimeSpan.Zero || band.ActivityCount < 0 ||
                !double.IsFinite(band.ShrinkageWeight) || band.ShrinkageWeight < 0 || !Enum.IsDefined(band.Confidence)) ||
            model.PowerModel.Bands.Select(band => (band.GradeKey, band.DurationKey)).Distinct().Count() != model.PowerModel.Bands.Count)
            throw new PredictionCalculationException("Prediction power model is invalid.");

        var coefficients = model.Coefficients;
        if (coefficients is null ||
            !double.IsFinite(coefficients.DrivetrainEfficiency) || coefficients.DrivetrainEfficiency <= 0 ||
            !double.IsFinite(coefficients.AirDensity) || coefficients.AirDensity <= 0 ||
            !double.IsFinite(coefficients.Crr) || coefficients.Crr < 0 ||
            !double.IsFinite(coefficients.CdA) || coefficients.CdA < 0 ||
            model.DescentLimits is null)
            throw new PredictionCalculationException("Prediction physical model is invalid.");
    }

    private static void ValidateDescent(DescentLimitEstimate descent)
    {
        if (descent is null || !Enum.IsDefined(descent.Confidence) ||
            double.IsNaN(descent.SpeedCapMetresPerSecond) || double.IsNegativeInfinity(descent.SpeedCapMetresPerSecond) ||
            (double.IsFinite(descent.SpeedCapMetresPerSecond) && descent.SpeedCapMetresPerSecond <= 0))
            throw new PredictionCalculationException("Prediction descent model is invalid.");
    }

    private static double HalveProposal(double proposal)
    {
        var halved = proposal / 2;
        if (!double.IsFinite(halved) || halved <= 0 || halved >= proposal)
            throw new PredictionCalculationException("Prediction could not make progress along the route.");
        return halved;
    }

    private static TimeSpan ToDuration(double seconds)
    {
        try
        {
            var duration = TimeSpan.FromSeconds(seconds);
            if (duration <= TimeSpan.Zero)
                throw new PredictionCalculationException("Prediction produced invalid segment time.");
            return duration;
        }
        catch (OverflowException)
        {
            throw new PredictionCalculationException("Prediction produced invalid segment time.");
        }
    }

    private static ConfidenceLevel RouteConfidence(IReadOnlyList<PredictionSegment> segments, bool wasCalibrated)
    {
        if (!wasCalibrated) return ConfidenceLevel.Low;
        var totalSeconds = segments.Sum(segment => segment.MovingTime.TotalSeconds);
        if (!double.IsFinite(totalSeconds) || totalSeconds <= 0)
            throw new PredictionCalculationException("Prediction produced invalid route time.");
        var highShare = segments.Where(segment => segment.Confidence == ConfidenceLevel.High)
            .Sum(segment => segment.MovingTime.TotalSeconds) / totalSeconds;
        var mediumOrHighShare = segments.Where(segment => segment.Confidence >= ConfidenceLevel.Medium)
            .Sum(segment => segment.MovingTime.TotalSeconds) / totalSeconds;
        return highShare >= .80
            ? ConfidenceLevel.High
            : mediumOrHighShare >= .80
                ? ConfidenceLevel.Medium
                : ConfidenceLevel.Low;
    }

    private static void AddWarning(string warning, ICollection<string> warnings, ISet<string> warningSet)
    {
        if (warningSet.Add(warning)) warnings.Add(warning);
    }

    private static ConfidenceLevel Min(ConfidenceLevel left, ConfidenceLevel right) =>
        (ConfidenceLevel)Math.Min((int)left, (int)right);

    private sealed record AcceptedSubstep(double ExitSpeedMetresPerSecond, double Seconds);
}
