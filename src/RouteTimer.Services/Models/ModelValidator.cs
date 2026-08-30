using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Physics;
using RouteTimer.Services.Routes;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Models;

/// <summary>
/// Performs whole-activity leave-one-out validation of a rider's power model (design doc section 10).
/// For each eligible training activity in turn, a fold-specific power model is built from every other
/// eligible activity, the held-out activity's own recorded route is predicted from scratch using that
/// fold model, and the predicted moving time is compared with the activity's actual recorded moving
/// time. The resulting per-fold absolute percentage errors are summarized as a median and 90th
/// percentile; the median against a 10% target determines whether the model passes or fails validation.
/// </summary>
public sealed class ModelValidator(
    IPowerModelBuilder builder,
    IPhysicsCalibrator calibrator,
    IDescentLimitBuilder descentBuilder,
    IRouteProcessor routeProcessor,
    IRoutePredictor predictor) : IModelValidator
{
    private const int MinimumEligibleActivities = 3;
    private const double PassingMedianAbsolutePercentageError = .10;

    /// <summary>
    /// Algorithm-version tag for the transient, per-fold <see cref="RiderModel"/> built during
    /// validation. This model is used once (to produce a single fold's prediction) and discarded - it
    /// is never persisted - so a fixed literal distinct from the real model's algorithm version is fine.
    /// </summary>
    private const string FoldAlgorithmVersion = "leave-one-out-fold";

    public ModelValidationSummary Validate(RiderProfile profile, IReadOnlyList<CleanedActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(activities);

        var eligible = activities.Where(activity => activity.Quality.Eligibility == ActivityEligibility.Eligible).ToList();
        if (eligible.Count < MinimumEligibleActivities)
        {
            return new ModelValidationSummary(ModelValidationStatus.InsufficientData, null, null);
        }

        var absolutePercentageErrors = new List<double>();
        for (var heldOutIndex = 0; heldOutIndex < eligible.Count; heldOutIndex++)
        {
            var heldOut = eligible[heldOutIndex];

            // Exclude the held-out activity by position, not equality: CleanedActivity is a record, so
            // two structurally identical activities would compare equal, and excluding "the held-out
            // activity" by value could accidentally drop every activity that looks like it too.
            var trainingSet = eligible.Where((_, index) => index != heldOutIndex).ToList();

            PowerModel foldPowerModel;
            try
            {
                foldPowerModel = builder.Build(profile, trainingSet);
            }
            catch (InvalidOperationException)
            {
                // The remaining N-1 activities happened to carry no usable power evidence between them.
                // Skip this fold rather than aborting the whole validation.
                continue;
            }

            var foldCalibration = calibrator.Calibrate(profile, trainingSet);
            var foldDescents = descentBuilder.Build(trainingSet);
            var foldModel = new RiderModel(
                foldPowerModel,
                foldCalibration.Coefficients,
                foldDescents,
                foldCalibration.WasCalibrated,
                FoldAlgorithmVersion);

            ProcessedRoute route;
            try
            {
                route = routeProcessor.Process(heldOut.Samples.Select(sample => sample.Position).ToList());
            }
            catch (RouteInputException)
            {
                // Degenerate GPS trace for this activity - skip the fold.
                continue;
            }

            PredictionResult prediction;
            try
            {
                prediction = predictor.Predict(PredictionRoute.FromProcessed(route), profile, foldModel);
            }
            catch (PredictionCalculationException)
            {
                // No physically valid solution for this fold - skip it.
                continue;
            }

            var actualSeconds = heldOut.MovingDuration.TotalSeconds;
            var predictedSeconds = prediction.MovingTime.TotalSeconds;
            absolutePercentageErrors.Add(Math.Abs(predictedSeconds - actualSeconds) / actualSeconds);
        }

        if (absolutePercentageErrors.Count == 0)
        {
            // There was enough raw evidence to attempt validation, but every fold failed to produce a
            // usable prediction. Distinct from InsufficientData: the evidence existed, the computation
            // just couldn't complete. Report honestly rather than faking a score.
            return new ModelValidationSummary(ModelValidationStatus.NotValidated, null, null);
        }

        absolutePercentageErrors.Sort();
        var median = Percentile(absolutePercentageErrors, .5);
        var p90 = Percentile(absolutePercentageErrors, .9);
        var status = median <= PassingMedianAbsolutePercentageError ? ModelValidationStatus.Passed : ModelValidationStatus.Failed;
        return new ModelValidationSummary(status, median, p90);
    }

    /// <summary>
    /// The p-th percentile of a pre-sorted (ascending) sequence, using linear interpolation between
    /// closest ranks - the standard definition matching NumPy's default and Excel's PERCENTILE.INC.
    /// </summary>
    private static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 1)
        {
            return sortedAscending[0];
        }

        var rank = p * (sortedAscending.Count - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);
        if (lowerIndex == upperIndex)
        {
            return sortedAscending[lowerIndex];
        }

        var weight = rank - lowerIndex;
        return sortedAscending[lowerIndex] + (weight * (sortedAscending[upperIndex] - sortedAscending[lowerIndex]));
    }
}
