using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Endpoints;

public sealed class ProblemDetailsTests
{
    [Fact]
    public async Task Invalid_profile_returns_field_level_problem_details_with_a_stable_code()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/profile", new
        {
            riderWeightKg = 29,
            bikeAndEquipmentWeightKg = 2
        });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-profile", body.RootElement.GetProperty("code").GetString());
        var errors = body.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("riderWeightKg", out _));
        Assert.True(errors.TryGetProperty("bikeAndEquipmentWeightKg", out _));
        Assert.False(errors.TryGetProperty("profile", out _));
    }

    [Fact]
    public async Task Legacy_training_upload_rejects_malformed_multipart_as_problem_details()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();
        using var malformed = new ByteArrayContent(Encoding.UTF8.GetBytes("not a multipart body"));
        malformed.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/form-data; boundary=expected");

        using var response = await client.PostAsync("/api/training/uploads", malformed);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("multipart-required", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task Prediction_submission_rejects_requests_over_the_bounded_multipart_limit()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var request = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[51 * 1024 * 1024]), "file", "route.gpx" }
        };

        using var response = await client.PostAsync("/api/predictions", request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("gpx-too-large", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Job_endpoint_exposes_the_final_safe_json_shape()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        await SeedProfileAndModelAsync(app.Services);
        using var client = app.CreateClient();
        using var submitted = await client.PostAsync("/api/predictions", GpxForm());
        var accepted = (await submitted.Content.ReadFromJsonAsync<PredictionSubmissionResponse>())!;
        await PublishAsync(app.Services, accepted.PredictionId);

        using var response = await client.GetAsync($"/api/jobs/{accepted.JobId}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PredictRoute", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(accepted.PredictionId, body.RootElement.GetProperty("subjectId").GetGuid());
        Assert.Equal(100, body.RootElement.GetProperty("progressPercent").GetInt32());
        Assert.Equal("completed", body.RootElement.GetProperty("progressStage").GetString());
        Assert.True(body.RootElement.TryGetProperty("startedAt", out _));
        Assert.True(body.RootElement.TryGetProperty("updatedAt", out _));
        Assert.True(body.RootElement.TryGetProperty("completedAt", out _));
        Assert.False(body.RootElement.TryGetProperty("workerId", out _));
    }

    [Theory]
    [InlineData("RouteTimer.Contracts.Training.TrainingUploadBatchResponse", "files")]
    [InlineData("RouteTimer.Contracts.Training.TrainingUploadFileResponse", "fileName")]
    [InlineData("RouteTimer.Contracts.Training.TrainingActivitySummaryResponse", "sourceFileName")]
    [InlineData("RouteTimer.Contracts.Training.TrainingActivityDetailResponse", "summary")]
    [InlineData("RouteTimer.Contracts.Models.ModelStatusResponse", "isReady")]
    [InlineData("RouteTimer.Contracts.Models.ModelRebuildResponse", "jobId")]
    [InlineData("RouteTimer.Contracts.Jobs.JobResponse", "progressPercent")]
    public void Final_contract_types_exist_and_serialize_with_camel_case(string typeName, string expectedJsonProperty)
    {
        var contractsAssembly = typeof(PredictionSubmissionResponse).Assembly;
        var type = contractsAssembly.GetType(typeName);

        Assert.NotNull(type);

        var instance = CreateInstance(type!);
        var json = JsonSerializer.Serialize(instance, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains($"\"{expectedJsonProperty}\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"WorkerId\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Program_composes_resource_endpoint_modules_instead_of_inline_route_handlers()
    {
        var programPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RouteTimer.Api", "Program.cs");
        var programSource = await File.ReadAllTextAsync(Path.GetFullPath(programPath));

        Assert.Contains("app.MapProfileEndpoints();", programSource, StringComparison.Ordinal);
        Assert.Contains("app.MapTrainingEndpoints();", programSource, StringComparison.Ordinal);
        Assert.Contains("app.MapModelsEndpoints();", programSource, StringComparison.Ordinal);
        Assert.Contains("app.MapPredictionEndpoints();", programSource, StringComparison.Ordinal);
        Assert.Contains("app.MapJobEndpoints();", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("app.MapGet(\"/api/profile\"", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("app.MapPost(\"/api/predictions\"", programSource, StringComparison.Ordinal);
    }

    private static object CreateInstance(Type type)
    {
        if (type.FullName == "RouteTimer.Contracts.Training.TrainingUploadBatchResponse")
        {
            var fileType = type.Assembly.GetType("RouteTimer.Contracts.Training.TrainingUploadFileResponse")!;
            var file = Activator.CreateInstance(fileType, "ride.fit", "accepted", Guid.NewGuid(), Guid.NewGuid(), null)!;
            var list = Array.CreateInstance(fileType, 1);
            list.SetValue(file, 0);
            return Activator.CreateInstance(type, list)!;
        }

        if (type.FullName == "RouteTimer.Contracts.Training.TrainingUploadFileResponse")
        {
            return Activator.CreateInstance(type, "ride.fit", "accepted", Guid.NewGuid(), Guid.NewGuid(), null)!;
        }

        if (type.FullName == "RouteTimer.Contracts.Training.TrainingActivitySummaryResponse")
        {
            return Activator.CreateInstance(
                type,
                Guid.NewGuid(),
                Guid.NewGuid(),
                "ride.fit",
                DateTimeOffset.Parse("2026-08-25T09:00:00Z"),
                DateTimeOffset.Parse("2026-08-25T10:00:00Z"),
                "Garmin",
                "Edge",
                1000d,
                50d,
                1800d,
                "eligible",
                1d,
                1d,
                1d,
                1d,
                new[] { "none" },
                DateTimeOffset.Parse("2026-08-25T10:05:00Z"))!;
        }

        if (type.FullName == "RouteTimer.Contracts.Training.TrainingActivityDetailResponse")
        {
            var summaryType = type.Assembly.GetType("RouteTimer.Contracts.Training.TrainingActivitySummaryResponse")!;
            var summary = CreateInstance(summaryType);
            return Activator.CreateInstance(type, summary, new Dictionary<string, int> { ["missing-power"] = 1 })!;
        }

        if (type.FullName == "RouteTimer.Contracts.Models.ModelStatusResponse")
        {
            var physicalType = type.Assembly.GetType("RouteTimer.Contracts.Models.PhysicalCoefficientsResponse");
            var bandType = type.Assembly.GetType("RouteTimer.Contracts.Models.PowerBandCoverageResponse");
            var jobType = type.Assembly.GetType("RouteTimer.Contracts.Jobs.JobResponse");
            var physical = physicalType is null ? null : Activator.CreateInstance(physicalType, 0.97d, 1.2d, 0.004d, 0.3d);
            var band = bandType is null ? null : Activator.CreateInstance(bandType, "flat", "short", 250d, 600d, 4, 0.5d, "high");
            var bands = bandType is null ? Array.Empty<object>() : Array.CreateInstance(bandType, 1);
            if (bandType is not null && band is not null)
            {
                bands.SetValue(band, 0);
            }

            var job = jobType is null
                ? null
                : Activator.CreateInstance(
                    jobType,
                    Guid.NewGuid(),
                    "BuildModel",
                    Guid.NewGuid(),
                    "Running",
                    60,
                    "building-power-model",
                    2,
                    DateTimeOffset.Parse("2026-08-25T09:00:00Z"),
                    DateTimeOffset.Parse("2026-08-25T09:05:00Z"),
                    DateTimeOffset.Parse("2026-08-25T09:06:00Z"),
                    null,
                    DateTimeOffset.Parse("2026-08-25T09:10:00Z"),
                    null,
                    null);

            return Activator.CreateInstance(
                type,
                true,
                null,
                Guid.NewGuid(),
                "v1",
                DateTimeOffset.Parse("2026-08-25T09:00:00Z"),
                true,
                true,
                "passed",
                0.05d,
                0.1d,
                physical,
                bands,
                12,
                3,
                job)!;
        }

        if (type.FullName == "RouteTimer.Contracts.Models.ModelRebuildResponse")
        {
            return Activator.CreateInstance(type, Guid.NewGuid())!;
        }

        if (type.FullName == "RouteTimer.Contracts.Jobs.JobResponse")
        {
            return Activator.CreateInstance(
                type,
                Guid.NewGuid(),
                "PredictRoute",
                Guid.NewGuid(),
                "Succeeded",
                100,
                "completed",
                1,
                DateTimeOffset.Parse("2026-08-25T09:00:00Z"),
                DateTimeOffset.Parse("2026-08-25T09:01:00Z"),
                DateTimeOffset.Parse("2026-08-25T09:02:00Z"),
                DateTimeOffset.Parse("2026-08-25T09:03:00Z"),
                null,
                null,
                null)!;
        }

        throw new InvalidOperationException($"No contract fixture is defined for {type.FullName}.");
    }

    private static MultipartFormDataContent GpxForm()
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("<gpx version=\"1.1\"/>")), "file", "route.gpx");
        return form;
    }

    private static async Task SeedProfileAndModelAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        await new ProfileRepository(context).SaveAsync(new RiderProfile(75, 10), CancellationToken.None);
        await new RiderModelRepository(context).SaveAsync(
            new RiderModel(new PowerModel([], 210), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1"),
            new RiderProfile(75, 10),
            new ModelValidationSummary(ModelValidationStatus.InsufficientData, null, null),
            CancellationToken.None);
    }

    private static async Task PublishAsync(IServiceProvider services, Guid predictionId)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        var job = await context.Jobs.SingleAsync(entity => entity.SubjectId == predictionId && entity.Type == JobType.PredictRoute.ToString());
        var now = DateTimeOffset.UtcNow;
        job.State = JobState.Running.ToString();
        job.ProgressPercent = 100;
        job.ProgressStage = "completed";
        job.AttemptCount = 1;
        job.StartedAt = now;
        job.UpdatedAt = now;
        job.WorkerId = "problem-details-test-worker";
        await context.SaveChangesAsync();

        Assert.True(await new PredictionRepository(context).TryPublishAsync(
            predictionId,
            job.Id,
            job.WorkerId,
            new PredictionPublication(
                100,
                5,
                TimeSpan.FromSeconds(20),
                5,
                200,
                ConfidenceLevel.Medium,
                ["default-coefficients"],
                [new PersistedPredictionSegment(1, 51.1, -2.1, 105, 75, 25, .04, .002, 190, 4, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), ConfidenceLevel.High)]),
            CancellationToken.None));

        job.State = JobState.Succeeded.ToString();
        job.CompletedAt = now;
        job.WorkerId = null;
        await context.SaveChangesAsync();
    }
}
