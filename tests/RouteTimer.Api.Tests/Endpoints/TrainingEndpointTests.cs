using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Contracts.Training;
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

public sealed class TrainingEndpointTests
{
    // Break caught: training resources were still only exposed through the staged upload route instead of the final resource paths.
    [Fact]
    public async Task List_and_detail_return_the_projected_training_resource_shapes()
    {
        await using var app = CreateRiderApp();
        var seeded = await SeedTrainingActivityAsync(app.Services);
        using var client = app.CreateClient();

        using var listResponse = await client.GetAsync("/api/training-activities");
        using var detailResponse = await client.GetAsync($"/api/training-activities/{seeded.ActivityId}");
        var summaries = await listResponse.Content.ReadFromJsonAsync<List<TrainingActivitySummaryResponse>>();
        var detail = await detailResponse.Content.ReadFromJsonAsync<TrainingActivityDetailResponse>();

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var summary = Assert.Single(summaries!);
        Assert.Equal(seeded.ActivityId, summary.Id);
        Assert.Equal(seeded.UploadId, summary.UploadId);
        Assert.Equal("ride.fit", summary.SourceFileName);
        Assert.Equal(seeded.StartedAt, summary.StartedAt);
        Assert.Equal(seeded.EndedAt, summary.EndedAt);
        Assert.Equal("Garmin", summary.DeviceManufacturer);
        Assert.Equal("Edge 1040", summary.DeviceProduct);
        Assert.Equal(24_500d, summary.DistanceMetres);
        Assert.Equal(410d, summary.AscentMetres);
        Assert.Equal(1800d, summary.MovingSeconds);
        Assert.Equal("Eligible", summary.Eligibility);
        Assert.Equal(0.98d, summary.PositionCoverage);
        Assert.Equal(0.97d, summary.ElevationCoverage);
        Assert.Equal(0.96d, summary.SpeedCoverage);
        Assert.Equal(0.95d, summary.PowerCoverage);
        Assert.Equal(["steady-effort"], summary.ReasonCodes);
        Assert.NotEqual(default, summary.CreatedAt);
        Assert.NotNull(detail);
        Assert.Equal(summary.Id, detail.Summary.Id);
        Assert.Equal(summary.UploadId, detail.Summary.UploadId);
        Assert.Equal(summary.SourceFileName, detail.Summary.SourceFileName);
        Assert.Equal(summary.StartedAt, detail.Summary.StartedAt);
        Assert.Equal(summary.EndedAt, detail.Summary.EndedAt);
        Assert.Equal(summary.DeviceManufacturer, detail.Summary.DeviceManufacturer);
        Assert.Equal(summary.DeviceProduct, detail.Summary.DeviceProduct);
        Assert.Equal(summary.DistanceMetres, detail.Summary.DistanceMetres);
        Assert.Equal(summary.AscentMetres, detail.Summary.AscentMetres);
        Assert.Equal(summary.MovingSeconds, detail.Summary.MovingSeconds);
        Assert.Equal(summary.Eligibility, detail.Summary.Eligibility);
        Assert.Equal(summary.PositionCoverage, detail.Summary.PositionCoverage);
        Assert.Equal(summary.ElevationCoverage, detail.Summary.ElevationCoverage);
        Assert.Equal(summary.SpeedCoverage, detail.Summary.SpeedCoverage);
        Assert.Equal(summary.PowerCoverage, detail.Summary.PowerCoverage);
        Assert.Equal(summary.ReasonCodes, detail.Summary.ReasonCodes);
        Assert.Equal(summary.CreatedAt, detail.Summary.CreatedAt);
        Assert.Equal(2, detail.ExclusionCounts["missing-power"]);
    }

    [Fact]
    public async Task Missing_detail_and_delete_return_not_found()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();

        using var detailResponse = await client.GetAsync($"/api/training-activities/{Guid.NewGuid()}");
        using var deleteResponse = await client.DeleteAsync($"/api/training-activities/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteResponse.StatusCode);
    }

    // Break caught: mixed training upload outcomes were still batch-failing or returning the staged 200 shape instead of one 202 resource response per file.
    [Fact]
    public async Task Upload_returns_202_with_one_result_per_file_and_keeps_invalid_files_inside_the_batch_response()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent([1, 2, 3]), "files", "accepted.fit" },
            { new ByteArrayContent([1, 2, 3]), "files", "duplicate.fit" },
            { new ByteArrayContent(new byte[50 * 1024 * 1024 + 1]), "files", "too-large.fit" }
        };

        using var response = await client.PostAsync("/api/training-activities", form);
        var batch = await response.Content.ReadFromJsonAsync<TrainingUploadBatchResponse>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/api/training-activities", response.Headers.Location?.OriginalString);
        Assert.NotNull(batch);
        Assert.Equal(3, batch.Files.Count);
        Assert.Collection(
            batch.Files,
            accepted =>
            {
                Assert.Equal("accepted.fit", accepted.FileName);
                Assert.Equal("accepted", accepted.Outcome);
                Assert.NotNull(accepted.UploadId);
                Assert.NotNull(accepted.JobId);
                Assert.Null(accepted.ErrorCode);
            },
            duplicate =>
            {
                Assert.Equal("duplicate.fit", duplicate.FileName);
                Assert.Equal("duplicate", duplicate.Outcome);
                Assert.Null(duplicate.UploadId);
                Assert.Null(duplicate.JobId);
                Assert.Equal("duplicate-upload", duplicate.ErrorCode);
            },
            invalid =>
            {
                Assert.Equal("too-large.fit", invalid.FileName);
                Assert.Equal("invalid", invalid.Outcome);
                Assert.Null(invalid.UploadId);
                Assert.Null(invalid.JobId);
                Assert.NotNull(invalid.ErrorCode);
            });
    }

    [Fact]
    public async Task Delete_returns_no_content_and_the_rebuild_job_is_visible_from_the_model_resource()
    {
        await using var app = CreateRiderApp();
        await SeedProfileAndModelAsync(app.Services);
        var seeded = await SeedTrainingActivityAsync(app.Services);
        using var client = app.CreateClient();

        using var deleteResponse = await client.DeleteAsync($"/api/training-activities/{seeded.ActivityId}");
        using var modelResponse = await client.GetAsync("/api/models/current");
        var model = await modelResponse.Content.ReadFromJsonAsync<RouteTimer.Contracts.Models.ModelStatusResponse>();

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, modelResponse.StatusCode);
        Assert.NotNull(model);
        Assert.True(model.IsReady);
        Assert.NotNull(model.RebuildJob);
        Assert.Equal("BuildModel", model.RebuildJob.Type);
        Assert.Equal(ModelSubject.Id, model.RebuildJob.SubjectId);
        Assert.Equal("Queued", model.RebuildJob.State);
    }

    [Fact]
    public async Task Legacy_training_upload_route_returns_not_found()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent([1, 2, 3]), "files", "ride.fit" }
        };

        using var response = await client.PostAsync("/api/training/uploads", form);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static RouteTimerApiFactory CreateRiderApp() => new RouteTimerApiFactory().WithRiderAuthentication();

    private static async Task<(Guid ActivityId, Guid UploadId, DateTimeOffset StartedAt, DateTimeOffset EndedAt)> SeedTrainingActivityAsync(
        IServiceProvider services,
        ActivityEligibility eligibility = ActivityEligibility.Eligible)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        var uploadId = Guid.NewGuid();
        context.Uploads.Add(new StoredUploadEntity
        {
            Id = uploadId,
            FileName = "ride.fit",
            Kind = "fit",
            Content = [1, 2, 3],
            Sha256 = new byte[32],
            CreatedAt = DateTimeOffset.Parse("2026-08-25T10:00:00Z")
        });
        await context.SaveChangesAsync();

        var startedAt = DateTimeOffset.Parse("2026-08-25T09:00:00Z");
        var endedAt = DateTimeOffset.Parse("2026-08-25T09:45:00Z");
        var activity = new CleanedActivity(
            "Morning ride",
            [new CleanRideSample(startedAt, TimeSpan.FromMinutes(30), new GeoPoint(51.5, -2.6, 100), 10, 220, 130, 88, false, 0.03, 0.001)],
            TimeSpan.FromMinutes(30),
            new ActivityQuality(eligibility, 0.98d, 0.97d, 0.96d, 0.95d, new Dictionary<string, int> { ["missing-power"] = 2 }, ["steady-effort"]),
            new TrainingActivityMetadata("ride.fit", startedAt, endedAt, "Garmin", "Edge 1040", 24_500d, 410d));

        var activityId = await new TrainingActivityRepository(context, TimeProvider.System).SaveAsync(uploadId, activity, CancellationToken.None);
        return (activityId, uploadId, startedAt, endedAt);
    }

    private static async Task SeedProfileAndModelAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        await new ProfileRepository(context).SaveAsync(new RiderProfile(75, 10), CancellationToken.None);
        await new RiderModelRepository(context).SaveAsync(
            new RiderModel(
                new PowerModel(
                    [new PowerBand("flat", "short", 250d, TimeSpan.FromMinutes(20), 4, 0.5d, ConfidenceLevel.High)],
                    210d),
                PhysicalCoefficients.Default,
                DescentLimitModel.Conservative,
                true,
                "v-test"),
            new RiderProfile(75, 10),
            new ModelValidationSummary(ModelValidationStatus.Passed, 0.04d, 0.08d),
            CancellationToken.None);
    }
}
