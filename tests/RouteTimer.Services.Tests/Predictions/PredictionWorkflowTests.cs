using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Tests.Predictions;

public sealed class PredictionWorkflowTests
{
    // Break caught: accepting a GPX before prerequisites exist leaves retained data or a job behind.
    [Theory]
    [InlineData(false, true, "profile-required")]
    [InlineData(true, false, "model-not-ready")]
    public async Task Submit_rejects_missing_prerequisite_without_persisting_prediction(bool hasProfile, bool hasModel, string expectedCode)
    {
        var predictions = new FakePredictionRepository();
        var service = new PredictionSubmissionService(
            new FixedProfileRepository(hasProfile ? new RiderProfile(75, 10) : null),
            new FixedModelRepository(hasModel ? ModelSnapshot("current", 210) : null),
            predictions,
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<PredictionSubmissionException>(() => service.SubmitAsync(Upload(), CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Empty(predictions.Created);
    }

    // Break caught: resolving either snapshot from the current profile/model after submission changes history.
    [Fact]
    public async Task Submit_captures_profile_model_and_reuses_identical_gpx_for_distinct_predictions()
    {
        var profiles = new FixedProfileRepository(new RiderProfile(75, 10));
        var models = new FixedModelRepository(ModelSnapshot("v1", 210));
        var predictions = new FakePredictionRepository();
        var service = new PredictionSubmissionService(profiles, models, predictions, TimeProvider.System);

        var first = await service.SubmitAsync(Upload(), CancellationToken.None);
        profiles.Profile = new RiderProfile(80, 11);
        models.Current = ModelSnapshot("v2", 260);
        var second = await service.SubmitAsync(Upload(), CancellationToken.None);

        Assert.NotEqual(first.PredictionId, second.PredictionId);
        Assert.Equal("v1", predictions.Created[0].Model.Model.AlgorithmVersion);
        Assert.Equal(new RiderProfile(75, 10), predictions.Created[0].Profile);
        Assert.Equal(predictions.Created[0].Upload.Sha256, predictions.Created[1].Upload.Sha256);
        Assert.Equal("v2", predictions.Created[1].Model.Model.AlgorithmVersion);
    }

    // Break caught: direct callers can retain an empty or over-limit GPX stream despite the documented upload boundary.
    [Theory]
    [InlineData("empty")]
    [InlineData("oversized")]
    public async Task Submit_rejects_empty_and_over_limit_gpx_streams(string kind)
    {
        var model = ModelSnapshot("current", 210);
        var service = new PredictionSubmissionService(new FixedProfileRepository(new RiderProfile(75, 10)), new FixedModelRepository(model), new FakePredictionRepository(), TimeProvider.System);
        await using var content = kind == "empty"
            ? new MemoryStream()
            : new MemoryStream(new byte[(50 * 1024 * 1024) + 1]);

        var exception = await Assert.ThrowsAsync<PredictionSubmissionException>(() => service.SubmitAsync(new PredictionUpload("route.gpx", content), CancellationToken.None));

        Assert.Equal(kind == "empty" ? "invalid-gpx-upload" : "gpx-too-large", exception.Code);
    }

    // Break caught: the worker loads the current model/profile instead of the model id and profile captured by the prediction.
    [Fact]
    public async Task Handler_uses_captured_model_and_profile_when_current_values_change()
    {
        var predictionId = Guid.NewGuid();
        var capturedModel = ModelSnapshot("captured", 210);
        var models = new FixedModelRepository(ModelSnapshot("current", 300)) { ById = { [capturedModel.Id] = capturedModel } };
        var predictions = new FakePredictionRepository
        {
            Processing = new PredictionForProcessing(
                predictionId,
                new StoredUpload(Guid.NewGuid(), "route.gpx", "gpx", GpxBytes(), new byte[32], DateTimeOffset.UtcNow),
                capturedModel.Id,
                new RiderProfile(75, 10))
        };
        var predictor = new CapturingPredictor();
        var handler = new PredictionJobHandler(predictions, models, new GpxRouteParser(), new RouteProcessor(RouteProcessingOptions.Default), predictor);
        var job = new AnalysisJob(Guid.NewGuid(), JobType.PredictRoute, predictionId, JobState.Running, 1, "worker", DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow);

        await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal(capturedModel.Model, predictor.Model);
        Assert.Equal(new RiderProfile(75, 10), predictor.Profile);
        Assert.NotNull(predictions.Published);
    }

    // Break caught: invalid predictor output is classified for the queue without committing prediction state separately.
    [Fact]
    public async Task Handler_classifies_invalid_prediction_without_persisting_failure_outside_the_queue()
    {
        var predictionId = Guid.NewGuid();
        var model = ModelSnapshot("captured", 210);
        var models = new FixedModelRepository(null) { ById = { [model.Id] = model } };
        var predictions = new FakePredictionRepository
        {
            Processing = new PredictionForProcessing(predictionId,
                new StoredUpload(Guid.NewGuid(), "route.gpx", "gpx", GpxBytes(), new byte[32], DateTimeOffset.UtcNow), model.Id, new RiderProfile(75, 10))
        };
        var handler = new PredictionJobHandler(predictions, models, new GpxRouteParser(), new RouteProcessor(RouteProcessingOptions.Default), new NegativePredictor());
        var job = new AnalysisJob(Guid.NewGuid(), JobType.PredictRoute, predictionId, JobState.Running, 1, "worker", DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<PredictionJobException>(() => handler.HandleAsync(job, CancellationToken.None));

        Assert.Equal("invalid-prediction-result", exception.Code);
        Assert.Null(predictions.Failure);
    }

    // Break caught: an uncalibrated or poor-validation model can be published as high confidence without a durable explanation.
    [Theory]
    [InlineData(false, ModelValidationStatus.Passed, ConfidenceLevel.Low, "uncalibrated-coefficients")]
    [InlineData(true, ModelValidationStatus.Failed, ConfidenceLevel.Low, "model-validation-failed")]
    [InlineData(true, ModelValidationStatus.InsufficientData, ConfidenceLevel.Medium, "model-validation-insufficient-data")]
    [InlineData(true, ModelValidationStatus.NotValidated, ConfidenceLevel.Medium, "model-validation-not-validated")]
    public async Task Handler_caps_confidence_and_persists_validation_warning(bool wasCalibrated, ModelValidationStatus validationStatus, ConfidenceLevel expectedConfidence, string expectedWarning)
    {
        var predictionId = Guid.NewGuid();
        var model = ModelSnapshot("captured", 210, wasCalibrated, validationStatus);
        var models = new FixedModelRepository(null) { ById = { [model.Id] = model } };
        var predictions = new FakePredictionRepository
        {
            Processing = new PredictionForProcessing(predictionId,
                new StoredUpload(Guid.NewGuid(), "route.gpx", "gpx", GpxBytes(), new byte[32], DateTimeOffset.UtcNow), model.Id, new RiderProfile(75, 10))
        };
        var handler = new PredictionJobHandler(predictions, models, new GpxRouteParser(), new RouteProcessor(RouteProcessingOptions.Default), new CapturingPredictor());

        await handler.HandleAsync(new AnalysisJob(Guid.NewGuid(), JobType.PredictRoute, predictionId, JobState.Running, 1, "worker", DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(expectedConfidence, predictions.Published!.Confidence);
        Assert.Contains(expectedWarning, predictions.Published.Warnings);
    }

    // Break caught: NaN output or mismatched segment/time data is persisted instead of becoming a permanent prediction failure.
    [Theory]
    [InlineData("non-finite", "invalid-prediction-result")]
    [InlineData("sequence", "prediction-sequence-mismatch")]
    [InlineData("time", "prediction-time-mismatch")]
    public async Task Handler_rejects_non_finite_sequence_and_time_inconsistencies(string kind, string expectedCode)
    {
        var predictionId = Guid.NewGuid();
        var model = ModelSnapshot("captured", 210);
        var predictions = new FakePredictionRepository
        {
            Processing = new PredictionForProcessing(predictionId,
                new StoredUpload(Guid.NewGuid(), "route.gpx", "gpx", GpxBytes(), new byte[32], DateTimeOffset.UtcNow), model.Id, new RiderProfile(75, 10))
        };
        var handler = new PredictionJobHandler(predictions, new FixedModelRepository(null) { ById = { [model.Id] = model } }, new GpxRouteParser(),
            new RouteProcessor(RouteProcessingOptions.Default), new InvalidResultPredictor(kind));

        var exception = await Assert.ThrowsAsync<PredictionJobException>(() => handler.HandleAsync(
            new AnalysisJob(Guid.NewGuid(), JobType.PredictRoute, predictionId, JobState.Running, 1, "worker", DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow), CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Null(predictions.Failure);
    }

    // Break caught: average predicted power gives equal influence to unequal-duration segments.
    [Fact]
    public async Task Handler_persists_time_weighted_average_power()
    {
        var predictionId = Guid.NewGuid();
        var model = ModelSnapshot("captured", 210);
        var predictions = new FakePredictionRepository
        {
            Processing = new PredictionForProcessing(predictionId,
            new StoredUpload(Guid.NewGuid(), "route.gpx", "gpx", GpxBytes(), new byte[32], DateTimeOffset.UtcNow), model.Id, new RiderProfile(75, 10))
        };
        var handler = new PredictionJobHandler(predictions, new FixedModelRepository(null) { ById = { [model.Id] = model } }, new GpxRouteParser(),
            new RouteProcessor(RouteProcessingOptions.Default), new UnequalDurationPredictor());

        await handler.HandleAsync(new AnalysisJob(Guid.NewGuid(), JobType.PredictRoute, predictionId, JobState.Running, 1, "worker", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(166.66666666666666, predictions.Published!.AveragePowerWatts, 10);
    }

    [Fact]
    public async Task Handler_leaves_transient_failures_for_the_queue_retry_policy()
    {
        var predictionId = Guid.NewGuid();
        var model = ModelSnapshot("captured", 210);
        var predictions = new FakePredictionRepository
        {
            Processing = new PredictionForProcessing(predictionId,
            new StoredUpload(Guid.NewGuid(), "route.gpx", "gpx", GpxBytes(), new byte[32], DateTimeOffset.UtcNow), model.Id, new RiderProfile(75, 10))
        };
        var handler = new PredictionJobHandler(predictions, new FixedModelRepository(null) { ById = { [model.Id] = model } }, new GpxRouteParser(), new RouteProcessor(RouteProcessingOptions.Default), new ThrowingPredictor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new AnalysisJob(Guid.NewGuid(), JobType.PredictRoute, predictionId, JobState.Running, 2, "worker", null, DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.Null(predictions.Failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new AnalysisJob(Guid.NewGuid(), JobType.PredictRoute, predictionId, JobState.Running, 3, "worker", null, DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.Null(predictions.Failure);
    }

    private static PredictionUpload Upload() => new("route.gpx", new MemoryStream(GpxBytes()));

    private static byte[] GpxBytes() => """
        <gpx version="1.1"><trk><trkseg>
          <trkpt lat="51.0000" lon="-2.0000"><ele>10</ele></trkpt>
          <trkpt lat="51.0003" lon="-2.0000"><ele>12</ele></trkpt>
        </trkseg></trk></gpx>
        """u8.ToArray();

    private static RiderModelSnapshot ModelSnapshot(string version, double watts, bool wasCalibrated = false, ModelValidationStatus validationStatus = ModelValidationStatus.InsufficientData)
    {
        var model = new RiderModel(new PowerModel([], watts), PhysicalCoefficients.Default, DescentLimitModel.Conservative, wasCalibrated, version);
        return new RiderModelSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, new RiderProfile(75, 10), model,
            new ModelValidationSummary(validationStatus, null, null));
    }

    private sealed class FixedProfileRepository(RiderProfile? profile) : IProfileRepository
    {
        public RiderProfile? Profile { get; set; } = profile;
        public Task<RiderProfile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Profile);
        public Task SaveAsync(RiderProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedModelRepository(RiderModelSnapshot? current) : IRiderModelRepository
    {
        public RiderModelSnapshot? Current { get; set; } = current;
        public Dictionary<Guid, RiderModelSnapshot> ById { get; } = [];
        public Task<Guid> SaveAsync(RiderModel model, RiderProfile profileSnapshot, ModelValidationSummary validation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RiderModelSnapshot?> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task<RiderModelSnapshot?> GetAsync(Guid modelId, CancellationToken cancellationToken) => Task.FromResult(ById.TryGetValue(modelId, out var model) ? model : null);
    }

    private sealed class FakePredictionRepository : IPredictionRepository
    {
        public List<QueuedPredictionCreation> Created { get; } = [];
        public PredictionForProcessing? Processing { get; init; }
        public PredictionPublication? Published { get; private set; }
        public (string Code, string Message)? Failure { get; private set; }
        public Task<QueuedPredictionSubmission> CreateQueuedAsync(QueuedPredictionCreation creation, CancellationToken cancellationToken)
        {
            Created.Add(creation);
            return Task.FromResult(new QueuedPredictionSubmission(Guid.NewGuid(), Guid.NewGuid(), creation.Model.Id));
        }

        public Task<PredictionForProcessing?> GetForProcessingAsync(Guid predictionId, CancellationToken cancellationToken) => Task.FromResult(Processing);
        public Task PublishAsync(Guid predictionId, PredictionPublication publication, CancellationToken cancellationToken) { Published = publication; return Task.CompletedTask; }
        public Task FailAsync(Guid predictionId, string code, string message, CancellationToken cancellationToken) { Failure = (code, message); return Task.CompletedTask; }
        public Task<IReadOnlyList<PredictionSummary>> GetSummariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PredictionDetail?> GetAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingPredictor : IRoutePredictor
    {
        public RiderProfile? Profile { get; private set; }
        public RiderModel? Model { get; private set; }
        public PredictionResult Predict(ProcessedRoute route, RiderProfile profile, RiderModel model)
        {
            Profile = profile;
            Model = model;
            var segments = route.Samples.Skip(1).Select(sample => new PredictionSegment(
                sample.Sequence,
                sample.SegmentDistanceMetres,
                sample.Gradient,
                200,
                5,
                TimeSpan.FromSeconds(sample.SegmentDistanceMetres / 5),
                ConfidenceLevel.High)).ToList();
            return new PredictionResult(segments, TimeSpan.FromSeconds(segments.Sum(segment => segment.MovingTime.TotalSeconds)), ConfidenceLevel.High);
        }
    }

    private sealed class NegativePredictor : IRoutePredictor
    {
        public PredictionResult Predict(ProcessedRoute route, RiderProfile profile, RiderModel model) => new(
            route.Samples.Skip(1).Select(sample => new PredictionSegment(sample.Sequence, sample.SegmentDistanceMetres, sample.Gradient, -1, 5, TimeSpan.FromSeconds(1), ConfidenceLevel.Low)).ToList(),
            TimeSpan.FromSeconds(route.Samples.Count - 1),
            ConfidenceLevel.Low);
    }

    private sealed class InvalidResultPredictor(string kind) : IRoutePredictor
    {
        public PredictionResult Predict(ProcessedRoute route, RiderProfile profile, RiderModel model)
        {
            var source = route.Samples.Skip(1).ToArray();
            if (kind == "sequence") return new PredictionResult([], TimeSpan.Zero, ConfidenceLevel.Low);
            var segments = source.Select(sample => new PredictionSegment(
                sample.Sequence,
                sample.SegmentDistanceMetres,
                sample.Gradient,
                kind == "non-finite" ? double.NaN : 200,
                5,
                TimeSpan.FromSeconds(1),
                ConfidenceLevel.Low)).ToList();
            return new PredictionResult(segments, kind == "time" ? TimeSpan.FromSeconds(99) : TimeSpan.FromSeconds(segments.Count), ConfidenceLevel.Low);
        }
    }

    private sealed class UnequalDurationPredictor : IRoutePredictor
    {
        public PredictionResult Predict(ProcessedRoute route, RiderProfile profile, RiderModel model)
        {
            var segments = route.Samples.Skip(1).Select((sample, index) => new PredictionSegment(sample.Sequence, sample.SegmentDistanceMetres, sample.Gradient,
                index == 0 ? 100 : 200, 5, TimeSpan.FromSeconds(index == 0 ? 10 : 20), ConfidenceLevel.Low)).ToList();
            return new PredictionResult(segments, TimeSpan.FromSeconds(30), ConfidenceLevel.Low);
        }
    }

    private sealed class ThrowingPredictor : IRoutePredictor
    {
        public PredictionResult Predict(ProcessedRoute route, RiderProfile profile, RiderModel model) => throw new InvalidOperationException("transient");
    }
}
