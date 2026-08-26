using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Endpoints;

public sealed class PredictionEndpointTests
{
    // Break caught: the newly exposed prediction and durable job resources accidentally bypass the fallback rider policy.
    [Theory]
    [InlineData("/api/predictions")]
    [InlineData("/api/predictions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("/api/jobs/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    public async Task Prediction_and_job_resources_require_authentication(string path)
    {
        await using var app = new RouteTimerApiFactory();
        using var client = app.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_non_rider_is_forbidden_from_api_resources()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "non-rider");

        using var response = await client.GetAsync("/api/predictions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Break caught: prediction submission still returns a synchronous preview rather than a queued durable resource.
    [Fact]
    public async Task Submission_returns_202_with_prediction_job_and_model_ids_then_exposes_summary_detail_and_job()
    {
        await using var app = CreateRiderApp();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("""
            <gpx version="1.1"><trk><trkseg><trkpt lat="51" lon="-2"><ele>10</ele></trkpt><trkpt lat="51.001" lon="-2"><ele>12</ele></trkpt></trkseg></trk></gpx>
            """)), "file", "route.gpx");

        using var submitted = await client.PostAsync("/api/predictions", form);
        var accepted = await submitted.Content.ReadFromJsonAsync<PredictionSubmissionResponse>();

        Assert.True(submitted.StatusCode == HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync());
        Assert.NotNull(accepted);
        Assert.NotEqual(Guid.Empty, accepted.PredictionId);
        Assert.NotEqual(Guid.Empty, accepted.JobId);
        Assert.NotEqual(Guid.Empty, accepted.ModelId);
        using var summaries = await client.GetAsync("/api/predictions");
        using var detail = await client.GetAsync($"/api/predictions/{accepted.PredictionId}");
        using var job = await client.GetAsync($"/api/jobs/{accepted.JobId}");
        Assert.Equal(HttpStatusCode.OK, summaries.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, job.StatusCode);
    }

    [Fact]
    public async Task Submission_maps_missing_profile_and_model_to_stable_conflicts()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();

        using var profileMissing = await client.PostAsync("/api/predictions", GpxForm());
        Assert.Equal(HttpStatusCode.Conflict, profileMissing.StatusCode);
        Assert.Contains("profile-required", await profileMissing.Content.ReadAsStringAsync());

        await SeedProfileAsync(app.Services);
        using var modelMissing = await client.PostAsync("/api/predictions", GpxForm());
        Assert.Equal(HttpStatusCode.Conflict, modelMissing.StatusCode);
        Assert.Contains("model-not-ready", await modelMissing.Content.ReadAsStringAsync());
    }

    // Break caught: more than one multipart file reaches SingleOrDefault and becomes an unhandled 500 instead of a stable client error.
    [Fact]
    public async Task Submission_rejects_multiple_files_with_a_stable_client_error()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("<gpx/>")), "first", "one.gpx" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("<gpx/>")), "second", "two.gpx" }
        };

        using var response = await client.PostAsync("/api/predictions", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("prediction-gpx-required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_prediction_and_job_return_not_found()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();

        using var missingPrediction = await client.GetAsync($"/api/predictions/{Guid.NewGuid()}");
        using var missingJob = await client.GetAsync($"/api/jobs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, missingPrediction.StatusCode);
        Assert.Contains("prediction-not-found", await missingPrediction.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, missingJob.StatusCode);
        Assert.Contains("job-not-found", await missingJob.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Gpx_download_returns_404_for_an_unknown_prediction()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();

        using var response = await client.GetAsync($"/api/predictions/{Guid.NewGuid()}/gpx");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("prediction-not-found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Gpx_download_returns_409_for_a_queued_prediction()
    {
        await using var app = CreateRiderApp();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var submitted = await client.PostAsync("/api/predictions", GpxForm());
        var accepted = await submitted.Content.ReadFromJsonAsync<PredictionSubmissionResponse>();

        using var response = await client.GetAsync($"/api/predictions/{accepted!.PredictionId}/gpx");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("prediction-not-complete", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Gpx_download_returns_the_untimed_course_track_by_default()
    {
        await using var app = CreateRiderApp();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var submitted = await client.PostAsync("/api/predictions", GpxForm());
        var accepted = await submitted.Content.ReadFromJsonAsync<PredictionSubmissionResponse>();
        await PublishAsync(app.Services, accepted!.PredictionId);

        using var response = await client.GetAsync($"/api/predictions/{accepted.PredictionId}/gpx");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/gpx+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.NotNull(response.Content.Headers.ContentDisposition);
        Assert.EndsWith(".gpx", response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<trkpt", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<time>", body.Split("<trkseg>")[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gpx_download_writes_predicted_times_when_timed_is_requested()
    {
        await using var app = CreateRiderApp();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var submitted = await client.PostAsync("/api/predictions", GpxForm());
        var accepted = await submitted.Content.ReadFromJsonAsync<PredictionSubmissionResponse>();
        await PublishAsync(app.Services, accepted!.PredictionId);

        using var response = await client.GetAsync($"/api/predictions/{accepted.PredictionId}/gpx?timed=true");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<time>", body.Split("<trkseg>")[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submission_rejects_empty_and_wrong_extension_uploads_with_stable_codes()
    {
        await using var app = CreateRiderApp();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var empty = new MultipartFormDataContent { { new ByteArrayContent([]), "file", "route.gpx" } };
        using var extension = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "route.txt" } };

        using var emptyResponse = await client.PostAsync("/api/predictions", empty);
        using var extensionResponse = await client.PostAsync("/api/predictions", extension);

        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);
        Assert.Contains("invalid-gpx-upload", await emptyResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, extensionResponse.StatusCode);
        Assert.Contains("prediction-gpx-required", await extensionResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Submission_accepts_a_31_mb_route_but_rejects_over_50_mb_and_malformed_multipart_with_stable_codes()
    {
        await using var app = CreateRiderApp();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var valid = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[31 * 1024 * 1024]), "file", "route.gpx" }
        };
        using var tooLarge = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[50 * 1024 * 1024 + 1]), "file", "route.gpx" }
        };
        using var missingBoundary = new ByteArrayContent([]);
        missingBoundary.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        using var malformed = new ByteArrayContent(Encoding.UTF8.GetBytes("not a multipart body"));
        malformed.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=expected");

        using var validResponse = await client.PostAsync("/api/predictions", valid);
        using var tooLargeResponse = await client.PostAsync("/api/predictions", tooLarge);
        using var missingBoundaryResponse = await client.PostAsync("/api/predictions", missingBoundary);
        using var malformedResponse = await client.PostAsync("/api/predictions", malformed);

        Assert.Equal(HttpStatusCode.Accepted, validResponse.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLargeResponse.StatusCode);
        Assert.Contains("gpx-too-large", await tooLargeResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, missingBoundaryResponse.StatusCode);
        var missingBoundaryBody = await missingBoundaryResponse.Content.ReadAsStringAsync();
        Assert.True(missingBoundaryBody.Contains("multipart-required", StringComparison.Ordinal), missingBoundaryBody);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        Assert.Contains("multipart-required", await malformedResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Submission_uses_the_production_kestrel_body_limit()
    {
        await using var app = CreateRiderApp();
        app.UseKestrel();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var valid = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[31 * 1024 * 1024]), "file", "route.gpx" }
        };

        using var response = await client.PostAsync("/api/predictions", valid);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Submission_does_not_translate_persistence_invalid_operation_into_multipart_error()
    {
        await using var app = CreateRiderApp(services =>
        {
            services.RemoveAll<IPredictionRepository>();
            services.AddScoped<IPredictionRepository, ThrowingPredictionRepository>();
        });
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsync("/api/predictions", GpxForm());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("multipart-required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task History_detail_and_job_contracts_expose_snapshots_ordered_segments_and_no_summary_segment_payload()
    {
        await using var app = CreateRiderApp();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var submitted = await client.PostAsync("/api/predictions", GpxForm());
        var accepted = (await submitted.Content.ReadFromJsonAsync<PredictionSubmissionResponse>())!;
        await PublishAsync(app.Services, accepted.PredictionId);
        await SeedProfileAsync(app.Services, new RiderProfile(80, 11));

        using var summaries = await client.GetAsync("/api/predictions");
        using var detail = await client.GetAsync($"/api/predictions/{accepted.PredictionId}");
        using var job = await client.GetAsync($"/api/jobs/{accepted.JobId}");
        using var summaryJson = JsonDocument.Parse(await summaries.Content.ReadAsStringAsync());
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        using var jobJson = JsonDocument.Parse(await job.Content.ReadAsStringAsync());

        var summary = summaryJson.RootElement[0];
        Assert.False(summary.TryGetProperty("segments", out _));
        Assert.Equal(75, summary.GetProperty("riderWeightKg").GetDouble());
        var segments = detailJson.RootElement.GetProperty("segments");
        Assert.Equal(1, segments[0].GetProperty("sequence").GetInt32());
        Assert.Equal(2, segments[1].GetProperty("sequence").GetInt32());
        Assert.Equal("PredictRoute", jobJson.RootElement.GetProperty("type").GetString());
        Assert.Equal("Succeeded", jobJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, jobJson.RootElement.GetProperty("attemptCount").GetInt32());
        Assert.Equal(100, jobJson.RootElement.GetProperty("progressPercent").GetInt32());
        Assert.Equal("completed", jobJson.RootElement.GetProperty("progressStage").GetString());
        Assert.True(jobJson.RootElement.TryGetProperty("createdAt", out _));
        Assert.True(jobJson.RootElement.TryGetProperty("subjectId", out var subjectId));
        Assert.Equal(accepted.PredictionId, subjectId.GetGuid());
        Assert.True(jobJson.RootElement.TryGetProperty("diagnosticCode", out var diagnosticCode));
        Assert.Equal(JsonValueKind.Null, diagnosticCode.ValueKind);
        Assert.True(jobJson.RootElement.TryGetProperty("diagnosticMessage", out var diagnosticMessage));
        Assert.Equal(JsonValueKind.Null, diagnosticMessage.ValueKind);
        Assert.True(jobJson.RootElement.TryGetProperty("leaseExpiresAt", out _));
        Assert.Equal(JsonValueKind.Null, jobJson.RootElement.GetProperty("leaseExpiresAt").ValueKind);
        Assert.False(jobJson.RootElement.TryGetProperty("workerId", out _));
    }

    [Fact]
    public async Task Delete_returns_no_content_cancels_the_active_job_and_removes_the_prediction_resource()
    {
        await using var app = CreateRiderApp();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var submitted = await client.PostAsync("/api/predictions", GpxForm());
        var accepted = (await submitted.Content.ReadFromJsonAsync<PredictionSubmissionResponse>())!;
        var runningLease = await MarkJobRunningAsync(app.Services, accepted.PredictionId, progressPercent: 45, progressStage: "processing-route");

        using var deleteResponse = await client.DeleteAsync($"/api/predictions/{accepted.PredictionId}");
        using var repeatedDelete = await client.DeleteAsync($"/api/predictions/{accepted.PredictionId}");
        using var detailAfterDelete = await client.GetAsync($"/api/predictions/{accepted.PredictionId}");
        using var jobAfterDelete = await client.GetAsync($"/api/jobs/{accepted.JobId}");
        var job = (await jobAfterDelete.Content.ReadFromJsonAsync<JobResponse>())!;

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, repeatedDelete.StatusCode);
        Assert.Contains("prediction-not-found", await repeatedDelete.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, detailAfterDelete.StatusCode);
        Assert.Contains("prediction-not-found", await detailAfterDelete.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, jobAfterDelete.StatusCode);
        Assert.Equal("Cancelled", job.State);
        Assert.Equal(45, job.ProgressPercent);
        Assert.Equal("cancelled", job.ProgressStage);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Null(job.DiagnosticCode);
        Assert.Null(job.DiagnosticMessage);
        Assert.NotEqual(runningLease, job.LeaseExpiresAt);
    }

    [Fact]
    public async Task Job_resource_exposes_lease_expiry_only_while_running()
    {
        await using var app = CreateRiderApp();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var submitted = await client.PostAsync("/api/predictions", GpxForm());
        var accepted = (await submitted.Content.ReadFromJsonAsync<PredictionSubmissionResponse>())!;
        var leaseExpiresAt = await MarkJobRunningAsync(app.Services, accepted.PredictionId, progressPercent: 35, progressStage: "simulating-route");

        using var jobResponse = await client.GetAsync($"/api/jobs/{accepted.JobId}");
        var job = (await jobResponse.Content.ReadFromJsonAsync<JobResponse>())!;
        using var jobJson = JsonDocument.Parse(await jobResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, jobResponse.StatusCode);
        Assert.Equal("Running", job.State);
        Assert.Equal(35, job.ProgressPercent);
        Assert.Equal("simulating-route", job.ProgressStage);
        Assert.Equal(leaseExpiresAt, job.LeaseExpiresAt);
        Assert.False(jobJson.RootElement.TryGetProperty("workerId", out _));
    }

    private static MultipartFormDataContent GpxForm()
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("<gpx version=\"1.1\"/>")), "file", "route.gpx");
        return form;
    }

    private static RouteTimerApiFactory CreateRiderApp(Action<IServiceCollection>? configure = null)
        => new RouteTimerApiFactory().WithRiderAuthentication(configure);

    private static async Task SeedProfileAndModelAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        var profiles = new ProfileRepository(context);
        await profiles.SaveAsync(new RiderProfile(75, 10), CancellationToken.None);
        var models = new RiderModelRepository(context);
        await models.SaveAsync(new RiderModel(new PowerModel([], 210), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1"), new RiderProfile(75, 10),
            new ModelValidationSummary(ModelValidationStatus.InsufficientData, null, null), CancellationToken.None);
    }

    private static async Task SeedProfileAsync(IServiceProvider services, RiderProfile? profile = null)
    {
        await using var scope = services.CreateAsyncScope();
        await new ProfileRepository(scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>()).SaveAsync(profile ?? new RiderProfile(75, 10), CancellationToken.None);
    }

    private static async Task PublishAsync(IServiceProvider services, Guid predictionId)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        var job = await context.Jobs.SingleAsync(entity => entity.SubjectId == predictionId && entity.Type == "PredictRoute");
        var now = DateTimeOffset.UtcNow;
        job.State = "Running";
        job.ProgressStage = "running";
        job.AttemptCount = 1;
        job.StartedAt = now;
        job.UpdatedAt = now;
        job.WorkerId = "endpoint-test-worker";
        await context.SaveChangesAsync();
        Assert.True(await new PredictionRepository(context).TryPublishAsync(predictionId, job.Id, job.WorkerId,
            new RouteTimer.Services.Persistence.PredictionPublication(100, 5, TimeSpan.FromSeconds(20), 5, 200, ConfidenceLevel.Medium, ["default-coefficients"],
                [new RouteTimer.Services.Persistence.PersistedPredictionSegment(2, 51.2, -2.2, 110, 100, 25, .05, .001, 200, 5, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), ConfidenceLevel.Medium),
                 new RouteTimer.Services.Persistence.PersistedPredictionSegment(1, 51.1, -2.1, 105, 75, 25, .04, .002, 190, 4, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), ConfidenceLevel.High)]), CancellationToken.None));
        job.State = "Succeeded";
        job.ProgressPercent = 100;
        job.ProgressStage = "completed";
        job.CompletedAt = now;
        job.WorkerId = null;
        await context.SaveChangesAsync();
    }

    private static async Task<DateTimeOffset> MarkJobRunningAsync(
        IServiceProvider services,
        Guid predictionId,
        int progressPercent,
        string progressStage)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        var job = await context.Jobs.SingleAsync(entity => entity.SubjectId == predictionId && entity.Type == JobType.PredictRoute.ToString());
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.AddMinutes(5);
        job.State = JobState.Running.ToString();
        job.ProgressPercent = progressPercent;
        job.ProgressStage = progressStage;
        job.AttemptCount = 1;
        job.StartedAt = now.AddMinutes(-1);
        job.UpdatedAt = now;
        job.WorkerId = "endpoint-test-worker";
        job.LeaseExpiresAt = leaseExpiresAt;
        await context.SaveChangesAsync();
        return leaseExpiresAt;
    }
    private sealed class ThrowingPredictionRepository : IPredictionRepository
    {
        public Task<QueuedPredictionSubmission> CreateQueuedAsync(QueuedPredictionCreation creation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated persistence failure");
        public Task<PredictionForProcessing?> GetForProcessingAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryPublishAsync(Guid predictionId, Guid jobId, string workerId, PredictionPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid predictionId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailAsync(Guid predictionId, string code, string message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PredictionSummary>> GetSummariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PredictionDetail?> GetAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RouteTimer.Services.Routes.PredictionGpxSource?> GetGpxSourceAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
