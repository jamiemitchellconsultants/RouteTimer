using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Contracts.Models;
using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Entities;
using RouteTimer.Persistence.Repositories;

namespace RouteTimer.Api.Tests.Endpoints;

public sealed class ModelEndpointTests
{
    // Break caught: the model resource was still unimplemented, so readiness and current-model projections were not available through the final API.
    [Fact]
    public async Task Current_returns_the_blocked_shape_when_the_profile_is_missing()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/models/current");
        var model = await response.Content.ReadFromJsonAsync<ModelStatusResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(model);
        Assert.False(model.IsReady);
        Assert.Equal("profile-required", model.BlockingReason);
        Assert.Null(model.ModelId);
        Assert.Null(model.AlgorithmVersion);
        Assert.Empty(model.PowerBands);
        Assert.Null(model.RebuildJob);
    }

    [Fact]
    public async Task Current_returns_the_ready_shape_with_bands_coefficients_and_rebuild_job()
    {
        await using var app = CreateRiderApp();
        var seeded = await SeedCurrentModelAsync(app.Services, includeRebuildJob: true);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/models/current");
        var model = await response.Content.ReadFromJsonAsync<ModelStatusResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(model);
        Assert.True(model.IsReady);
        Assert.Null(model.BlockingReason);
        Assert.Equal(seeded.ModelId, model.ModelId);
        Assert.Equal("v-ready", model.AlgorithmVersion);
        Assert.Equal(seeded.CreatedAt, model.CreatedAt);
        Assert.True(model.WasCalibrated);
        Assert.False(model.DescentWasLearned);
        Assert.Equal("Passed", model.ValidationStatus);
        Assert.Equal(0.03d, model.ValidationMedianAbsolutePercentageError);
        Assert.Equal(0.07d, model.ValidationP90AbsolutePercentageError);
        Assert.NotNull(model.PhysicalCoefficients);
        Assert.Equal(0.97d, model.PhysicalCoefficients.DrivetrainEfficiency);
        Assert.Equal(1.225d, model.PhysicalCoefficients.AirDensity);
        Assert.Equal(0.005d, model.PhysicalCoefficients.RollingCoefficient);
        Assert.Equal(0.32d, model.PhysicalCoefficients.CdA);
        var band = Assert.Single(model.PowerBands);
        Assert.Equal("flat", band.GradeKey);
        Assert.Equal("short", band.DurationKey);
        Assert.Equal(260d, band.TypicalWatts);
        Assert.Equal(900d, band.EvidenceSeconds);
        Assert.Equal(5, band.ActivityCount);
        Assert.Equal(0.4d, band.ShrinkageWeight);
        Assert.Equal("Medium", band.Confidence);
        Assert.Equal(0, model.LearnedDescentCellCount);
        Assert.Equal(9, model.FallbackDescentCellCount);
        Assert.NotNull(model.RebuildJob);
        Assert.Equal("BuildModel", model.RebuildJob.Type);
        Assert.Equal("Running", model.RebuildJob.State);
        Assert.Equal("building-power-model", model.RebuildJob.ProgressStage);
    }

    [Fact]
    public async Task Rebuild_returns_202_with_the_coalesced_job_id()
    {
        await using var app = CreateRiderApp();
        await SeedProfileAsync(app.Services);
        await SeedTrainingActivityAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsync("/api/models/rebuild", content: null);
        var rebuild = await response.Content.ReadFromJsonAsync<ModelRebuildResponse>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(rebuild);
        Assert.NotEqual(Guid.Empty, rebuild.JobId);
    }

    [Fact]
    public async Task Rebuild_maps_missing_profile_and_training_prerequisites_to_stable_conflicts()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();

        using var missingProfile = await client.PostAsync("/api/models/rebuild", content: null);
        Assert.Equal(HttpStatusCode.Conflict, missingProfile.StatusCode);
        Assert.Contains("profile-required", await missingProfile.Content.ReadAsStringAsync());

        await SeedProfileAsync(app.Services);
        using var missingTraining = await client.PostAsync("/api/models/rebuild", content: null);
        Assert.Equal(HttpStatusCode.Conflict, missingTraining.StatusCode);
        Assert.Contains("no-eligible-activities", await missingTraining.Content.ReadAsStringAsync());
    }

    private static RouteTimerApiFactory CreateRiderApp() => new RouteTimerApiFactory().WithRiderAuthentication();

    private static async Task SeedProfileAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await new ProfileRepository(scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>())
            .SaveAsync(new RiderProfile(75, 10), CancellationToken.None);
    }

    private static async Task SeedTrainingActivityAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        var uploadId = Guid.NewGuid();
        context.Uploads.Add(new StoredUploadEntity
        {
            Id = uploadId,
            FileName = "activity.fit",
            Kind = "fit",
            Content = [1, 2, 3],
            Sha256 = new byte[32],
            CreatedAt = DateTimeOffset.Parse("2026-08-25T11:00:00Z")
        });
        await context.SaveChangesAsync();

        var startedAt = DateTimeOffset.Parse("2026-08-25T10:00:00Z");
        var activity = new CleanedActivity(
            "Eligible ride",
            [new CleanRideSample(startedAt, TimeSpan.FromMinutes(40), new GeoPoint(51.5, -2.6, 120), 9.5d, 215, 128, 86, false)],
            TimeSpan.FromMinutes(40),
            new ActivityQuality(ActivityEligibility.Eligible, 0.99d, 0.98d, 0.97d, 0.96d, new Dictionary<string, int>(), ["steady-effort"]),
            new TrainingActivityMetadata("activity.fit", startedAt, startedAt.AddMinutes(40), "Garmin", "Edge", 21_000d, 320d));

        await new TrainingActivityRepository(context, TimeProvider.System).SaveAsync(uploadId, activity, CancellationToken.None);
    }

    private static async Task<(Guid ModelId, DateTimeOffset CreatedAt)> SeedCurrentModelAsync(IServiceProvider services, bool includeRebuildJob)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        await new ProfileRepository(context).SaveAsync(new RiderProfile(75, 10), CancellationToken.None);
        var repository = new RiderModelRepository(context);
        var modelId = await repository.SaveAsync(
            new RiderModel(
                new PowerModel(
                    [new PowerBand("flat", "short", 260d, TimeSpan.FromMinutes(15), 5, 0.4d, ConfidenceLevel.Medium)],
                    230d),
                PhysicalCoefficients.Default,
                DescentLimitModel.Conservative,
                true,
                "v-ready"),
            new RiderProfile(75, 10),
            new ModelValidationSummary(ModelValidationStatus.Passed, 0.03d, 0.07d),
            CancellationToken.None);

        var snapshot = await repository.GetCurrentAsync(CancellationToken.None);
        Assert.NotNull(snapshot);

        if (includeRebuildJob)
        {
            var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
            context.Jobs.Add(new AnalysisJobEntity
            {
                Id = Guid.NewGuid(),
                Type = JobType.BuildModel.ToString(),
                SubjectId = ModelSubject.Id,
                State = JobState.Running.ToString(),
                ProgressPercent = 60,
                ProgressStage = "building-power-model",
                AttemptCount = 2,
                CreatedAt = now.AddMinutes(-10),
                StartedAt = now.AddMinutes(-5),
                UpdatedAt = now,
                LeaseExpiresAt = now.AddMinutes(5)
            });
            await context.SaveChangesAsync();
        }

        return (modelId, snapshot.CreatedAt);
    }
}
