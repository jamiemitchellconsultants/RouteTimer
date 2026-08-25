using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Garmin;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Endpoints;

public sealed class GarminActivityEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Activities_requires_authentication()
    {
        await using var app = new RouteTimerApiFactory();
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/garmin/activities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Activities_requires_the_rider_role()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "non-rider");

        using var response = await client.GetAsync("/api/garmin/activities");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Break caught: the endpoint could expose adapter/token fields, omit safe metrics, or return disallowed activity types.
    [Fact]
    public async Task Activities_returns_a_token_free_filtered_page_with_imported_state()
    {
        var adapter = new FakeAdapterClient
        {
            Page = new GarminAdapterActivityPage(
                [
                    Activity("road", "Road ride", "road-cycling", 42000, 3600, 550, 240),
                    Activity("run", "Run", "running", 5000, 1200, 50, null),
                    Activity("gravel", "Gravel ride", "gravel-cycling", null, null, null, null),
                    Activity("similar", "Indoor", "road-cycling-indoor", 10000, 900, 0, 180)
                ],
                100,
                "rotated-secret-token")
        };
        await using var app = CreateRiderApp(adapter);
        await SeedConnectionAndImportAsync(app.Services, "gravel");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/garmin/activities?cursor=NTA");
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(50, adapter.LastOffset);
        Assert.Equal("saved-secret-token", adapter.LastTokenJson);
        Assert.Equal(["activities", "nextCursor"], body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("MTAw", body.RootElement.GetProperty("nextCursor").GetString());
        var activities = body.RootElement.GetProperty("activities").EnumerateArray().ToArray();
        Assert.Equal(2, activities.Length);
        Assert.Equal(
            ["activityId", "name", "startedAt", "activityType", "distanceMetres", "durationSeconds", "ascentMetres", "averagePowerWatts", "alreadyImported"],
            activities[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal("road", activities[0].GetProperty("activityId").GetString());
        Assert.Equal("Road ride", activities[0].GetProperty("name").GetString());
        Assert.Equal("road-cycling", activities[0].GetProperty("activityType").GetString());
        Assert.Equal(42000, activities[0].GetProperty("distanceMetres").GetDouble());
        Assert.Equal(3600, activities[0].GetProperty("durationSeconds").GetDouble());
        Assert.Equal(550, activities[0].GetProperty("ascentMetres").GetDouble());
        Assert.Equal(240, activities[0].GetProperty("averagePowerWatts").GetDouble());
        Assert.False(activities[0].GetProperty("alreadyImported").GetBoolean());
        Assert.Equal("gravel", activities[1].GetProperty("activityId").GetString());
        Assert.True(activities[1].GetProperty("alreadyImported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, activities[1].GetProperty("distanceMetres").ValueKind);
        Assert.DoesNotContain("rotated-secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("saved-secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Activities_without_a_saved_connection_returns_connection_required()
    {
        var adapter = new FakeAdapterClient();
        await using var app = CreateRiderApp(adapter);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/garmin/activities");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("garmin-connection-required", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, adapter.ActivityCalls);
    }

    [Fact]
    public async Task Activities_with_reconnect_required_state_returns_reconnect_required()
    {
        var adapter = new FakeAdapterClient();
        await using var app = CreateRiderApp(adapter);
        await SaveConnectionAsync(app.Services, "reconnect-required");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/garmin/activities");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("garmin-reconnect-required", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, adapter.ActivityCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("MDE")]
    [InlineData("LTE")]
    [InlineData("MTAwMDAwMDAx")]
    public async Task Activities_with_invalid_cursor_returns_stable_bad_request(string cursor)
    {
        var adapter = new FakeAdapterClient();
        await using var app = CreateRiderApp(adapter);
        await SaveConnectionAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.GetAsync($"/api/garmin/activities?cursor={Uri.EscapeDataString(cursor)}");
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("garmin-cursor-invalid", body.RootElement.GetProperty("code").GetString());
        if (cursor.Length > 0)
        {
            Assert.DoesNotContain(cursor, json, StringComparison.Ordinal);
        }
        Assert.Equal(0, adapter.ActivityCalls);
    }

    [Theory]
    [InlineData(GarminAdapterError.Authentication, HttpStatusCode.Conflict, "garmin-reconnect-required")]
    [InlineData(GarminAdapterError.RateLimited, HttpStatusCode.TooManyRequests, "garmin-rate-limited")]
    [InlineData(GarminAdapterError.Unavailable, HttpStatusCode.ServiceUnavailable, "garmin-unavailable")]
    [InlineData(GarminAdapterError.AdapterUnavailable, HttpStatusCode.ServiceUnavailable, "garmin-adapter-unavailable")]
    [InlineData(GarminAdapterError.ResponseInvalid, HttpStatusCode.BadGateway, "garmin-response-invalid")]
    [InlineData(GarminAdapterError.RequestInvalid, HttpStatusCode.BadGateway, "garmin-response-invalid")]
    public async Task Activities_maps_adapter_failures_without_leaking_private_details(
        GarminAdapterError error,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        const string privateDetail = "raw token SQL stack http://garmin-adapter.internal";
        var adapter = new FakeAdapterClient
        {
            ActivityException = new GarminAdapterException(error, privateDetail)
        };
        await using var app = CreateRiderApp(adapter);
        await SaveConnectionAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/garmin/activities");
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(privateDetail, json, StringComparison.Ordinal);
        Assert.DoesNotContain("garmin-adapter.internal", json, StringComparison.Ordinal);
    }

    private static RouteTimerApiFactory CreateRiderApp(FakeAdapterClient adapter) =>
        new RouteTimerApiFactory().WithRiderAuthentication(services =>
        {
            services.RemoveAll<IGarminAdapterClient>();
            services.AddSingleton<IGarminAdapterClient>(adapter);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        });

    private static async Task SeedConnectionAndImportAsync(IServiceProvider services, string activityId)
    {
        await SaveConnectionAsync(services);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        var uploadId = Guid.NewGuid();
        context.Uploads.Add(new StoredUploadEntity
        {
            Id = uploadId,
            Kind = "fit",
            FileName = "gravel.fit",
            Content = [1, 2, 3],
            Sha256 = Enumerable.Repeat((byte)7, 32).ToArray(),
            CreatedAt = Now
        });
        context.GarminActivityImports.Add(new GarminActivityImportEntity
        {
            GarminActivityId = activityId,
            UploadId = uploadId,
            ActivityName = "Gravel ride",
            LinkedAt = Now
        });
        await context.SaveChangesAsync();
    }

    private static async Task SaveConnectionAsync(IServiceProvider services, string state = "connected")
    {
        await using var scope = services.CreateAsyncScope();
        var protector = scope.ServiceProvider.GetRequiredService<IGarminTokenProtector>();
        var repository = scope.ServiceProvider.GetRequiredService<IGarminConnectionRepository>();
        await repository.SaveAsync(
            new GarminConnectionRecord(state, "42", "Jamie", protector.Protect("saved-secret-token"), Now, Now),
            CancellationToken.None);
    }

    private static GarminAdapterActivity Activity(
        string id,
        string name,
        string type,
        double? distance,
        double? duration,
        double? ascent,
        double? power) =>
        new(id, name, Now.AddHours(-1), type, distance, duration, ascent, power);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAdapterClient : IGarminAdapterClient
    {
        public GarminAdapterActivityPage Page { get; set; } = new([], null, "saved-secret-token");
        public GarminAdapterException? ActivityException { get; set; }
        public int ActivityCalls { get; private set; }
        public int? LastOffset { get; private set; }
        public string? LastTokenJson { get; private set; }

        public Task<GarminAdapterActivityPage> GetActivitiesAsync(
            string tokenJson,
            int offset,
            CancellationToken cancellationToken)
        {
            ActivityCalls++;
            LastOffset = offset;
            LastTokenJson = tokenJson;
            return ActivityException is null
                ? Task.FromResult(Page)
                : Task.FromException<GarminAdapterActivityPage>(ActivityException);
        }

        public Task<GarminAdapterLogin> LoginAsync(string email, string password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GarminAdapterLogin> CompleteMfaAsync(string challengeId, string code, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GarminAdapterSession> ValidateAsync(string tokenJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GarminAdapterActivityResult> GetActivityAsync(string tokenJson, string activityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GarminAdapterFitDownload> DownloadFitAsync(string tokenJson, string activityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ClearChallengesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
