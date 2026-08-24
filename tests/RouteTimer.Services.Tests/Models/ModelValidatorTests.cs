using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Models;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Routes;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Tests.Models;

public sealed class ModelValidatorTests
{
    private static readonly RiderProfile SampleProfile = new(75, 10);
    private static readonly PowerModel SampleModel = ModelFixtures.SimpleModel();

    [Fact]
    public void Validate_reports_insufficient_data_with_fewer_than_three_eligible_activities_and_never_invokes_dependencies()
    {
        var activities = new List<CleanedActivity> { Activity(1000), Activity(1000), IneligibleActivity() };
        var builder = new FakePowerModelBuilder([]);
        var routeProcessor = new FakeRouteProcessor([]);
        var predictor = new FakeRoutePredictor([]);
        var validator = new ModelValidator(builder, routeProcessor, predictor);

        var result = validator.Validate(SampleProfile, activities);

        Assert.Equal(ModelValidationStatus.InsufficientData, result.Status);
        Assert.Null(result.MedianAbsolutePercentageError);
        Assert.Null(result.P90AbsolutePercentageError);
        Assert.Equal(0, builder.CallCount);
        Assert.Equal(0, routeProcessor.CallCount);
        Assert.Equal(0, predictor.CallCount);
    }

    [Fact]
    public void Validate_computes_median_and_p90_by_interpolation_across_all_successful_folds()
    {
        // 4 eligible activities, all actual = 1000s. Predicted times chosen to give ascending APEs of
        // 0.05, 0.15, 0.25, 0.35 in fold order (fold i holds out activity i).
        var activities = new List<CleanedActivity> { Activity(1000), Activity(1000), Activity(1000), Activity(1000) };
        var builder = new FakePowerModelBuilder([_ => SampleModel, _ => SampleModel, _ => SampleModel, _ => SampleModel]);
        var routeProcessor = new FakeRouteProcessor([_ => EmptyRoute(), _ => EmptyRoute(), _ => EmptyRoute(), _ => EmptyRoute()]);
        var predictor = new FakeRoutePredictor(
        [
            (_, _, _) => PredictionOf(1050),
            (_, _, _) => PredictionOf(1150),
            (_, _, _) => PredictionOf(1250),
            (_, _, _) => PredictionOf(1350),
        ]);
        var validator = new ModelValidator(builder, routeProcessor, predictor);

        var result = validator.Validate(SampleProfile, activities);

        // sorted errors: [0.05, 0.15, 0.25, 0.35]
        // median: rank = 0.5*3 = 1.5 -> 0.15 + 0.5*(0.25-0.15) = 0.20
        // p90: rank = 0.9*3 = 2.7 -> 0.25 + 0.7*(0.35-0.25) = 0.32
        Assert.NotNull(result.MedianAbsolutePercentageError);
        Assert.Equal(0.20, result.MedianAbsolutePercentageError!.Value, 10);
        Assert.Equal(0.32, result.P90AbsolutePercentageError!.Value, 10);
        Assert.Equal(ModelValidationStatus.Failed, result.Status);
        Assert.Equal(4, builder.CallCount);
        Assert.Equal(4, routeProcessor.CallCount);
        Assert.Equal(4, predictor.CallCount);
    }

    [Fact]
    public void Validate_passes_when_median_ape_is_exactly_at_the_ten_percent_target()
    {
        // 3 eligible activities, actual = 1000s. Predicted chosen for APEs [0.05, 0.10, 0.20] in fold
        // order, so the exact-rank median (no interpolation needed) lands exactly on 0.10.
        var activities = new List<CleanedActivity> { Activity(1000), Activity(1000), Activity(1000) };
        var builder = new FakePowerModelBuilder([_ => SampleModel, _ => SampleModel, _ => SampleModel]);
        var routeProcessor = new FakeRouteProcessor([_ => EmptyRoute(), _ => EmptyRoute(), _ => EmptyRoute()]);
        var predictor = new FakeRoutePredictor(
        [
            (_, _, _) => PredictionOf(1050),
            (_, _, _) => PredictionOf(1100),
            (_, _, _) => PredictionOf(1200),
        ]);
        var validator = new ModelValidator(builder, routeProcessor, predictor);

        var result = validator.Validate(SampleProfile, activities);

        Assert.Equal(0.10, result.MedianAbsolutePercentageError!.Value, 10);
        Assert.Equal(ModelValidationStatus.Passed, result.Status);
    }

    [Fact]
    public void Validate_fails_when_median_ape_is_just_over_the_ten_percent_target()
    {
        var activities = new List<CleanedActivity> { Activity(1000), Activity(1000), Activity(1000) };
        var builder = new FakePowerModelBuilder([_ => SampleModel, _ => SampleModel, _ => SampleModel]);
        var routeProcessor = new FakeRouteProcessor([_ => EmptyRoute(), _ => EmptyRoute(), _ => EmptyRoute()]);
        var predictor = new FakeRoutePredictor(
        [
            (_, _, _) => PredictionOf(1050),
            (_, _, _) => PredictionOf(1110),
            (_, _, _) => PredictionOf(1200),
        ]);
        var validator = new ModelValidator(builder, routeProcessor, predictor);

        var result = validator.Validate(SampleProfile, activities);

        Assert.Equal(0.11, result.MedianAbsolutePercentageError!.Value, 10);
        Assert.Equal(ModelValidationStatus.Failed, result.Status);
    }

    [Fact]
    public void Validate_skips_a_fold_whose_route_processing_throws_and_still_scores_the_rest()
    {
        // 4 eligible activities. Fold index 1's route processing throws RouteInputException and is
        // skipped; the other three folds succeed with APEs [0.05, 0.15, 0.35] (fold order 0, 2, 3).
        var activities = new List<CleanedActivity> { Activity(1000), Activity(1000), Activity(1000), Activity(1000) };
        var builder = new FakePowerModelBuilder([_ => SampleModel, _ => SampleModel, _ => SampleModel, _ => SampleModel]);
        var routeProcessor = new FakeRouteProcessor(
        [
            _ => EmptyRoute(),
            _ => throw new RouteInputException("degenerate trace"),
            _ => EmptyRoute(),
            _ => EmptyRoute(),
        ]);
        var predictor = new FakeRoutePredictor(
        [
            (_, _, _) => PredictionOf(1050),
            (_, _, _) => PredictionOf(1350),
            (_, _, _) => PredictionOf(1350),
        ]);
        var validator = new ModelValidator(builder, routeProcessor, predictor);

        var result = validator.Validate(SampleProfile, activities);

        // sorted successful errors: [0.05, 0.35, 0.35] -> median (rank 1.0) = 0.35
        Assert.Equal(0.35, result.MedianAbsolutePercentageError!.Value, 10);
        Assert.Equal(4, builder.CallCount);
        Assert.Equal(4, routeProcessor.CallCount);
        Assert.Equal(3, predictor.CallCount);
    }

    [Fact]
    public void Validate_skips_a_fold_whose_prediction_throws()
    {
        var activities = new List<CleanedActivity> { Activity(1000), Activity(1000), Activity(1000) };
        var builder = new FakePowerModelBuilder([_ => SampleModel, _ => SampleModel, _ => SampleModel]);
        var routeProcessor = new FakeRouteProcessor([_ => EmptyRoute(), _ => EmptyRoute(), _ => EmptyRoute()]);
        var predictor = new FakeRoutePredictor(
        [
            (_, _, _) => PredictionOf(1050),
            (_, _, _) => throw new PredictionCalculationException("no valid solution"),
            (_, _, _) => PredictionOf(1200),
        ]);
        var validator = new ModelValidator(builder, routeProcessor, predictor);

        var result = validator.Validate(SampleProfile, activities);

        // remaining errors: [0.05, 0.20] -> median interpolates at rank 0.5 = 0.125
        Assert.Equal(0.125, result.MedianAbsolutePercentageError!.Value, 10);
        Assert.NotEqual(ModelValidationStatus.NotValidated, result.Status);
    }

    [Fact]
    public void Validate_skips_a_fold_whose_model_build_throws()
    {
        var activities = new List<CleanedActivity> { Activity(1000), Activity(1000), Activity(1000) };
        var builder = new FakePowerModelBuilder(
        [
            _ => SampleModel,
            _ => throw new InvalidOperationException("no power evidence"),
            _ => SampleModel,
        ]);
        var routeProcessor = new FakeRouteProcessor([_ => EmptyRoute(), _ => EmptyRoute()]);
        var predictor = new FakeRoutePredictor([(_, _, _) => PredictionOf(1050), (_, _, _) => PredictionOf(1200)]);
        var validator = new ModelValidator(builder, routeProcessor, predictor);

        var result = validator.Validate(SampleProfile, activities);

        Assert.Equal(3, builder.CallCount);
        Assert.Equal(2, routeProcessor.CallCount);
        Assert.Equal(2, predictor.CallCount);
        Assert.NotNull(result.MedianAbsolutePercentageError);
    }

    [Fact]
    public void Validate_reports_not_validated_when_every_fold_fails()
    {
        var activities = new List<CleanedActivity> { Activity(1000), Activity(1000), Activity(1000) };
        var builder = new FakePowerModelBuilder([_ => SampleModel, _ => SampleModel, _ => SampleModel]);
        var routeProcessor = new FakeRouteProcessor(
        [
            _ => throw new RouteInputException("degenerate trace"),
            _ => throw new RouteInputException("degenerate trace"),
            _ => throw new RouteInputException("degenerate trace"),
        ]);
        var predictor = new FakeRoutePredictor([]);
        var validator = new ModelValidator(builder, routeProcessor, predictor);

        var result = validator.Validate(SampleProfile, activities);

        Assert.Equal(ModelValidationStatus.NotValidated, result.Status);
        Assert.Null(result.MedianAbsolutePercentageError);
        Assert.Null(result.P90AbsolutePercentageError);
        Assert.Equal(0, predictor.CallCount);
    }

    [Fact]
    public void Validate_excludes_the_held_out_activity_by_position_not_by_equality()
    {
        // The same CleanedActivity instance appears twice (a stand-in for two structurally identical
        // activities - they'd compare Equal to each other either way). If exclusion were done by value
        // equality (or reference equality) rather than by position, holding out either occurrence would
        // remove BOTH occurrences from the training set, leaving only 1 activity instead of 2. Assert
        // every fold's training set has exactly eligibleCount - 1 = 2 activities.
        var identical = Activity(1000);
        var distinct = Activity(2000);
        var activities = new List<CleanedActivity> { identical, identical, distinct };

        var builder = new FakePowerModelBuilder([_ => SampleModel, _ => SampleModel, _ => SampleModel]);
        var routeProcessor = new FakeRouteProcessor([_ => EmptyRoute(), _ => EmptyRoute(), _ => EmptyRoute()]);
        var predictor = new FakeRoutePredictor(
        [
            (_, _, _) => PredictionOf(1050),
            (_, _, _) => PredictionOf(1050),
            (_, _, _) => PredictionOf(2100),
        ]);
        var validator = new ModelValidator(builder, routeProcessor, predictor);

        validator.Validate(SampleProfile, activities);

        Assert.Equal(3, builder.ReceivedTrainingSets.Count);
        Assert.All(builder.ReceivedTrainingSets, trainingSet => Assert.Equal(2, trainingSet.Count));
    }

    private static CleanedActivity Activity(double movingSeconds)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            new CleanRideSample(start, TimeSpan.Zero, new GeoPoint(51, -2, 100), 7, 200, null, null, false, 0),
            new CleanRideSample(start.AddSeconds(movingSeconds), TimeSpan.FromSeconds(movingSeconds), new GeoPoint(51.01, -2, 100), 7, 200, null, null, false, 0),
        };
        return new CleanedActivity("Ride", samples, TimeSpan.FromSeconds(movingSeconds), new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []));
    }

    private static CleanedActivity IneligibleActivity()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new[] { new CleanRideSample(start, TimeSpan.Zero, new GeoPoint(51, -2, 100), 7, null, null, null, false, 0) };
        return new CleanedActivity("Short Ride", samples, TimeSpan.FromMinutes(1), new ActivityQuality(ActivityEligibility.Ineligible, 0.1, 0.1, 0.1, 0, new Dictionary<string, int> { ["gap"] = 1 }, ["too-short"]));
    }

    private static ProcessedRoute EmptyRoute() => new([], 0, 0);

    private static PredictionResult PredictionOf(double movingSeconds) => new([], TimeSpan.FromSeconds(movingSeconds), ConfidenceLevel.High);

    private sealed class FakePowerModelBuilder : IPowerModelBuilder
    {
        private readonly Queue<Func<IReadOnlyList<CleanedActivity>, PowerModel>> _handlers;

        public FakePowerModelBuilder(IEnumerable<Func<IReadOnlyList<CleanedActivity>, PowerModel>> handlers) =>
            _handlers = new Queue<Func<IReadOnlyList<CleanedActivity>, PowerModel>>(handlers);

        public int CallCount { get; private set; }
        public List<IReadOnlyList<CleanedActivity>> ReceivedTrainingSets { get; } = [];

        public PowerModel Build(RiderProfile profile, IReadOnlyList<CleanedActivity> activities)
        {
            CallCount++;
            ReceivedTrainingSets.Add(activities);
            if (_handlers.Count == 0)
            {
                throw new InvalidOperationException("Unexpected extra IPowerModelBuilder.Build call in test fake.");
            }

            return _handlers.Dequeue()(activities);
        }
    }

    private sealed class FakeRouteProcessor : IRouteProcessor
    {
        private readonly Queue<Func<IReadOnlyList<GeoPoint>, ProcessedRoute>> _handlers;

        public FakeRouteProcessor(IEnumerable<Func<IReadOnlyList<GeoPoint>, ProcessedRoute>> handlers) =>
            _handlers = new Queue<Func<IReadOnlyList<GeoPoint>, ProcessedRoute>>(handlers);

        public int CallCount { get; private set; }

        public ProcessedRoute Process(IReadOnlyList<GeoPoint> points)
        {
            CallCount++;
            if (_handlers.Count == 0)
            {
                throw new InvalidOperationException("Unexpected extra IRouteProcessor.Process call in test fake.");
            }

            return _handlers.Dequeue()(points);
        }
    }

    private sealed class FakeRoutePredictor : IRoutePredictor
    {
        private readonly Queue<Func<ProcessedRoute, RiderProfile, RiderModel, PredictionResult>> _handlers;

        public FakeRoutePredictor(IEnumerable<Func<ProcessedRoute, RiderProfile, RiderModel, PredictionResult>> handlers) =>
            _handlers = new Queue<Func<ProcessedRoute, RiderProfile, RiderModel, PredictionResult>>(handlers);

        public int CallCount { get; private set; }

        public PredictionResult Predict(ProcessedRoute route, RiderProfile profile, RiderModel model)
        {
            CallCount++;
            if (_handlers.Count == 0)
            {
                throw new InvalidOperationException("Unexpected extra IRoutePredictor.Predict call in test fake.");
            }

            return _handlers.Dequeue()(route, profile, model);
        }
    }
}
