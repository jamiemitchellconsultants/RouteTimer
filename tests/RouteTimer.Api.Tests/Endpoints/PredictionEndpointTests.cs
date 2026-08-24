using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RouteTimer.Contracts.Predictions;
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
        await using var app = new WebApplicationFactory<Program>();
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

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/predictions/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/jobs/{Guid.NewGuid()}")).StatusCode);
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
        Assert.Equal("Queued", jobJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(0, jobJson.RootElement.GetProperty("attemptCount").GetInt32());
        Assert.True(jobJson.RootElement.TryGetProperty("createdAt", out _));
        Assert.True(jobJson.RootElement.TryGetProperty("leaseExpiresAt", out _));
    }

    private static MultipartFormDataContent GpxForm()
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("<gpx version=\"1.1\"/>")), "file", "route.gpx");
        return form;
    }

    private static WebApplicationFactory<Program> CreateRiderApp(Action<IServiceCollection>? configure = null)
    {
        var databaseName = Guid.NewGuid().ToString();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<RouteTimerDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<RouteTimerDbContext>>();
            services.AddDbContext<RouteTimerDbContext>(options => options.UseInMemoryDatabase(databaseName));
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, RiderAuthenticationHandler>("test", _ => { });
            configure?.Invoke(services);
        }));
    }

    private static async Task SeedProfileAndModelAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        var profiles = new ProfileRepository(context);
        await profiles.SaveAsync(new RiderProfile(75, 10), CancellationToken.None);
        var models = new RiderModelRepository(context);
        await models.SaveAsync(new RiderModel(new PowerModel([], 210), PhysicalCoefficients.Default, "v1"), new RiderProfile(75, 10), false,
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
        await new PredictionRepository(scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>()).PublishAsync(predictionId,
            new RouteTimer.Services.Persistence.PredictionPublication(100, 5, TimeSpan.FromSeconds(20), 5, 200, ConfidenceLevel.Medium, ["default-coefficients"],
                [new RouteTimer.Services.Persistence.PersistedPredictionSegment(2, 51.2, -2.2, 110, 100, 25, .05, .001, 200, 5, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), ConfidenceLevel.Medium),
                 new RouteTimer.Services.Persistence.PersistedPredictionSegment(1, 51.1, -2.1, 105, 75, 25, .04, .002, 190, 4, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), ConfidenceLevel.High)]), CancellationToken.None);
    }

    private sealed class RiderAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, "rider") };
            if (Request.Headers["X-Test-Role"] != "non-rider") claims.Add(new Claim(ClaimTypes.Role, "rider"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class ThrowingPredictionRepository : IPredictionRepository
    {
        public Task<QueuedPredictionSubmission> CreateQueuedAsync(QueuedPredictionCreation creation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated persistence failure");
        public Task<PredictionForProcessing?> GetForProcessingAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PublishAsync(Guid predictionId, PredictionPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailAsync(Guid predictionId, string code, string message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PredictionSummary>> GetSummariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PredictionDetail?> GetAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
