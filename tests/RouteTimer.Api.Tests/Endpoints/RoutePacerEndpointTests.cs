using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Api.Tests.RoutePacer;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Domain.Models;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.RoutePacer;
using RouteTimer.Services.Routes;

namespace RouteTimer.Api.Tests.Endpoints;

public sealed class RoutePacerEndpointTests
{
    private const string StatusPath = "/api/routepacer/status";
    private static readonly Guid PredictionId = Guid.Parse("2f1a5b7c-0d3e-4f10-9a2b-3c4d5e6f7a8b");
    private static readonly string HandoffPath = $"/api/predictions/{PredictionId}/routepacer-handoff";
    private static readonly Uri PayloadUrl = new("https://pacetracking.tqaentry.com/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

    [Fact]
    public async Task RoutePacer_status_requires_authentication()
    {
        await using var app = new RouteTimerApiFactory();
        using var client = app.CreateClient();

        using var response = await client.GetAsync(StatusPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RoutePacer_handoff_requires_authentication()
    {
        await using var app = new RouteTimerApiFactory();
        using var client = app.CreateClient();

        using var response = await client.PostAsync(HandoffPath, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_non_rider_is_forbidden()
    {
        await using var app = Enabled();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "non-rider");

        using var response = await client.GetAsync(StatusPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // RouteTimer stays private: there is no anonymous route through which a phone could fetch a
    // payload from this application, whatever the handoff configuration says.
    [Theory]
    [InlineData("/api/routepacer/payloads/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task No_anonymous_payload_endpoint_exists(string path)
    {
        await using var app = Enabled();
        using var client = app.CreateClient();

        using var response = await client.GetAsync(path);

        // The SPA fallback answers unmatched GETs, so the proof is the absence of GPX, not a 404.
        Assert.NotEqual("application/gpx+xml", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Status_reports_disabled_but_still_names_the_origin()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();

        var status = await client.GetFromJsonAsync<RoutePacerStatusResponse>(StatusPath);

        Assert.False(status!.Enabled);
        Assert.Equal("https://pacetracking.tqaentry.com", status.RoutePacerOrigin);
    }

    [Fact]
    public async Task Status_reports_enabled_when_configured()
    {
        await using var app = Enabled();
        using var client = app.CreateClient();

        var status = await client.GetFromJsonAsync<RoutePacerStatusResponse>(StatusPath);

        Assert.True(status!.Enabled);
        Assert.Equal("https://pacetracking.tqaentry.com", status.RoutePacerOrigin);
    }

    [Fact]
    public async Task Handoff_returns_the_signed_url_and_relay_expiry()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        await using var app = Enabled(relay: new StubRelayClient(new RoutePacerRelayGrant(PayloadUrl, expiresAt)));
        using var client = app.CreateClient();

        using var response = await client.PostAsync(HandoffPath, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var handoff = await response.Content.ReadFromJsonAsync<RoutePacerHandoffResponse>();
        Assert.StartsWith("https://pacetracking.tqaentry.com/open?src=rt&v=1&payload=", handoff!.Url, StringComparison.Ordinal);
        Assert.Equal(expiresAt.ToUnixTimeMilliseconds(), handoff.ExpiresAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Disabled_handoff_is_service_unavailable()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication(WithPrediction(DefaultSource()));
        using var client = app.CreateClient();

        using var response = await client.PostAsync(HandoffPath, null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("routepacer-handoff-disabled", await CodeAsync(response));
    }

    [Fact]
    public async Task Missing_prediction_is_not_found()
    {
        await using var app = Enabled(withSource: false);
        using var client = app.CreateClient();

        using var response = await client.PostAsync(HandoffPath, null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("prediction-not-found", await CodeAsync(response));
    }

    [Fact]
    public async Task Incomplete_prediction_is_conflict()
    {
        await using var app = Enabled(source: new PredictionGpxSource("Empty", "Empty", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));
        using var client = app.CreateClient();

        using var response = await client.PostAsync(HandoffPath, null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("prediction-not-complete", await CodeAsync(response));
    }

    [Theory]
    [InlineData(RoutePacerRelayFailure.Authentication, HttpStatusCode.BadGateway, "routepacer-relay-authentication-failed")]
    [InlineData(RoutePacerRelayFailure.PayloadTooLarge, HttpStatusCode.RequestEntityTooLarge, "routepacer-payload-too-large")]
    [InlineData(RoutePacerRelayFailure.RejectedPayload, HttpStatusCode.BadGateway, "routepacer-relay-rejected-payload")]
    [InlineData(RoutePacerRelayFailure.RateLimited, HttpStatusCode.ServiceUnavailable, "routepacer-relay-rate-limited")]
    [InlineData(RoutePacerRelayFailure.Unavailable, HttpStatusCode.BadGateway, "routepacer-relay-unavailable")]
    [InlineData(RoutePacerRelayFailure.InvalidResponse, HttpStatusCode.BadGateway, "routepacer-relay-unavailable")]
    public async Task Relay_failures_map_to_stable_public_problems(
        RoutePacerRelayFailure failure,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        await using var app = Enabled(relay: new StubRelayClient(
            failure: new RoutePacerRelayException(failure, "internal detail about the relay")));
        using var client = app.CreateClient();

        using var response = await client.PostAsync(HandoffPath, null);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, await CodeAsync(response));
        Assert.DoesNotContain("internal detail about the relay", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limited_handoff_copies_a_valid_retry_after()
    {
        await using var app = Enabled(relay: new StubRelayClient(
            failure: new RoutePacerRelayException(RoutePacerRelayFailure.RateLimited, "rate limited", TimeSpan.FromSeconds(45))));
        using var client = app.CreateClient();

        using var response = await client.PostAsync(HandoffPath, null);

        Assert.Equal("45", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    [Fact]
    public async Task Rate_limited_handoff_without_a_retry_after_sends_no_header()
    {
        await using var app = Enabled(relay: new StubRelayClient(
            failure: new RoutePacerRelayException(RoutePacerRelayFailure.RateLimited, "rate limited")));
        using var client = app.CreateClient();

        using var response = await client.PostAsync(HandoffPath, null);

        Assert.False(response.Headers.Contains("Retry-After"));
    }

    // The handoff is a state-changing POST, so the existing global CSRF middleware must cover it
    // exactly as it covers every other non-GET endpoint.
    [Fact]
    public async Task Cross_site_handoff_requests_are_rejected()
    {
        await using var app = Enabled();
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, HandoffPath);
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("cross-site-request-rejected", await CodeAsync(response));
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static PredictionGpxSource DefaultSource() => new(
        "Kingston to Dorking",
        "Predicted route",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddHours(-1),
        [
            new PersistedPredictionSegment(0, 51.4085, -0.3064, 100, 0, 0, 0, 0, 200, 8, TimeSpan.FromSeconds(10), TimeSpan.Zero, ConfidenceLevel.High),
            new PersistedPredictionSegment(1, 51.4090, -0.3070, 115, 500, 500, 0, 0, 200, 8, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), ConfidenceLevel.High)
        ]);

    private static RouteTimerApiFactory Enabled(
        IRoutePacerRelayClient? relay = null,
        PredictionGpxSource? source = null,
        bool withSource = true) =>
        new RouteTimerApiFactory()
            .WithSetting("RoutePacerHandoff:Enabled", "true")
            .WithSetting("RoutePacerHandoff:RelayUploadKey", "test-upload-key")
            .WithSetting("RoutePacerHandoff:SigningPrivateKeyPem", RoutePacerHandoffOptionsTests.TestPrivateKeyPem)
            .WithRiderAuthentication(services =>
            {
                WithPrediction(withSource ? source ?? DefaultSource() : null)(services);
                services.RemoveAll<IRoutePacerRelayClient>();
                services.AddSingleton(relay ?? new StubRelayClient(
                    new RoutePacerRelayGrant(PayloadUrl, DateTimeOffset.UtcNow.AddMinutes(10))));
            });

    /// <summary>A null source is the absent prediction, not "use the default".</summary>
    private static Action<IServiceCollection> WithPrediction(PredictionGpxSource? source) => services =>
    {
        services.RemoveAll<IPredictionRepository>();
        services.AddScoped<IPredictionRepository>(_ => new StubPredictionRepository(source));
    };

    private sealed class StubRelayClient(RoutePacerRelayGrant? grant = null, RoutePacerRelayException? failure = null)
        : IRoutePacerRelayClient
    {
        public Task<RoutePacerRelayGrant> UploadAsync(byte[] timedGpx, CancellationToken cancellationToken) =>
            failure is not null
                ? throw failure
                : Task.FromResult(grant ?? throw new InvalidOperationException("No grant configured."));
    }

    private sealed class StubPredictionRepository(PredictionGpxSource? source) : IPredictionRepository
    {
        public Task<PredictionGpxSource?> GetGpxSourceAsync(Guid predictionId, CancellationToken cancellationToken) =>
            Task.FromResult(source);

        public Task<QueuedPredictionSubmission> CreateQueuedAsync(QueuedPredictionCreation creation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PredictionForProcessing?> GetForProcessingAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryPublishAsync(Guid predictionId, Guid jobId, string workerId, PredictionPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid predictionId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailAsync(Guid predictionId, string code, string message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PredictionSummary>> GetSummariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PredictionDetail?> GetAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordGarminCourseAsync(Guid predictionId, long courseId, DateTimeOffset uploadedAt, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
