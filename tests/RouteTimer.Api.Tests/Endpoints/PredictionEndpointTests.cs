using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
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

    [Fact]
    public async Task Missing_prediction_and_job_return_not_found()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/predictions/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/jobs/{Guid.NewGuid()}")).StatusCode);
    }

    private static MultipartFormDataContent GpxForm()
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("<gpx version=\"1.1\"/>")), "file", "route.gpx");
        return form;
    }

    private static WebApplicationFactory<Program> CreateRiderApp()
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

    private static async Task SeedProfileAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await new ProfileRepository(scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>()).SaveAsync(new RiderProfile(75, 10), CancellationToken.None);
    }

    private sealed class RiderAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "rider"), new Claim(ClaimTypes.Role, "rider")], Scheme.Name)), Scheme.Name)));
    }
}
