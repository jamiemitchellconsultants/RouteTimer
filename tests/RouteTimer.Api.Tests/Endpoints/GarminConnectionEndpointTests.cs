using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Contracts.Errors;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Garmin;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Endpoints;

public sealed class GarminConnectionEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Get_returns_a_token_free_disconnected_state()
    {
        var adapter = new FakeAdapterClient();
        await using var app = CreateRiderApp(adapter);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/garmin/connection");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("disconnected", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("garminUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("challengeId").ValueKind);
        AssertTokenFree(body.RootElement);
        Assert.Equal(0, adapter.ValidateCalls);
    }

    [Fact]
    public async Task Login_persists_only_encrypted_tokens_and_returns_safe_identity()
    {
        var adapter = new FakeAdapterClient
        {
            LoginResult = new GarminAdapterLogin("connected", null, "raw-token-json", "42", "Jamie")
        };
        await using var app = CreateRiderApp(adapter);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/garmin/connection/login", new
        {
            email = "rider@example.com",
            password = "top-secret-password"
        });
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("connected", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("42", body.RootElement.GetProperty("garminUserId").GetString());
        Assert.Equal("Jamie", body.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("challengeId").ValueKind);
        AssertTokenFree(body.RootElement);
        Assert.DoesNotContain("raw-token-json", json, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("rider@example.com", json, StringComparison.Ordinal);

        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        var saved = await context.GarminConnections.SingleAsync();
        Assert.DoesNotContain("raw-token-json", System.Text.Encoding.UTF8.GetString(saved.Ciphertext), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_returns_only_the_opaque_challenge_when_mfa_is_required()
    {
        var adapter = new FakeAdapterClient
        {
            LoginResult = new GarminAdapterLogin("mfa-required", "opaque-challenge", null, null, null)
        };
        await using var app = CreateRiderApp(adapter);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/garmin/connection/login", new
        {
            email = "rider@example.com",
            password = "top-secret-password"
        });
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("mfa-required", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("opaque-challenge", body.RootElement.GetProperty("challengeId").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("garminUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("displayName").ValueKind);
        AssertTokenFree(body.RootElement);
        Assert.DoesNotContain("top-secret-password", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mfa_completion_persists_the_connection_without_returning_the_code_or_token()
    {
        var adapter = new FakeAdapterClient
        {
            MfaResult = new GarminAdapterLogin("connected", null, "mfa-token-json", "42", "Jamie")
        };
        await using var app = CreateRiderApp(adapter);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/garmin/connection/mfa", new
        {
            challengeId = "opaque-challenge",
            code = "739201"
        });
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("connected", body.RootElement.GetProperty("state").GetString());
        AssertTokenFree(body.RootElement);
        Assert.DoesNotContain("739201", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mfa-token-json", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("login-email", HttpStatusCode.BadRequest, "garmin-credentials-rejected")]
    [InlineData("login-password", HttpStatusCode.BadRequest, "garmin-credentials-rejected")]
    [InlineData("mfa-challenge", HttpStatusCode.Conflict, "garmin-challenge-expired")]
    [InlineData("mfa-code", HttpStatusCode.BadRequest, "garmin-mfa-invalid")]
    public async Task Empty_authentication_fields_return_stable_safe_problems(
        string scenario,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var adapter = new FakeAdapterClient();
        await using var app = CreateRiderApp(adapter);
        using var client = app.CreateClient();
        var request = scenario switch
        {
            "login-email" => client.PostAsJsonAsync("/api/garmin/connection/login", new { email = " ", password = "not-returned" }),
            "login-password" => client.PostAsJsonAsync("/api/garmin/connection/login", new { email = "not-returned@example.com", password = " " }),
            "mfa-challenge" => client.PostAsJsonAsync("/api/garmin/connection/mfa", new { challengeId = " ", code = "not-returned" }),
            "mfa-code" => client.PostAsJsonAsync("/api/garmin/connection/mfa", new { challengeId = "not-returned", code = " " }),
            _ => throw new InvalidOperationException("Unknown test scenario.")
        };

        using var response = await request;
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("not-returned", json, StringComparison.Ordinal);
        Assert.Equal(0, adapter.LoginCalls + adapter.MfaCalls);
    }

    [Theory]
    [InlineData("login", GarminAdapterError.CredentialsRejected, HttpStatusCode.BadRequest, "garmin-credentials-rejected")]
    [InlineData("mfa", GarminAdapterError.MfaInvalid, HttpStatusCode.BadRequest, "garmin-mfa-invalid")]
    [InlineData("mfa", GarminAdapterError.ChallengeExpired, HttpStatusCode.Conflict, "garmin-challenge-expired")]
    [InlineData("login", GarminAdapterError.RateLimited, HttpStatusCode.TooManyRequests, "garmin-rate-limited")]
    [InlineData("login", GarminAdapterError.Unavailable, HttpStatusCode.ServiceUnavailable, "garmin-unavailable")]
    [InlineData("login", GarminAdapterError.AdapterUnavailable, HttpStatusCode.ServiceUnavailable, "garmin-adapter-unavailable")]
    [InlineData("login", GarminAdapterError.ResponseInvalid, HttpStatusCode.BadGateway, "garmin-response-invalid")]
    [InlineData("login", GarminAdapterError.RequestInvalid, HttpStatusCode.BadGateway, "garmin-response-invalid")]
    public async Task Adapter_failures_map_to_stable_safe_public_problems(
        string operation,
        GarminAdapterError error,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        const string privateDetail = "secret token SQL stack http://garmin-adapter.internal";
        var adapter = new FakeAdapterClient();
        if (operation == "login")
        {
            adapter.LoginException = new GarminAdapterException(error, privateDetail);
        }
        else
        {
            adapter.MfaException = new GarminAdapterException(error, privateDetail);
        }

        await using var app = CreateRiderApp(adapter);
        using var client = app.CreateClient();
        using var response = operation == "login"
            ? await client.PostAsJsonAsync("/api/garmin/connection/login", new { email = "rider@example.com", password = "password" })
            : await client.PostAsJsonAsync("/api/garmin/connection/mfa", new { challengeId = "challenge", code = "123456" });
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(privateDetail, json, StringComparison.Ordinal);
        Assert.DoesNotContain("garmin-adapter.internal", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deterministic_refresh_failure_returns_conflict_and_marks_reconnect_required()
    {
        var adapter = new FakeAdapterClient
        {
            ValidateException = new GarminAdapterException(GarminAdapterError.Authentication, "raw token and stack")
        };
        await using var app = CreateRiderApp(adapter);
        await SaveConnectionAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/garmin/connection");
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("garmin-reconnect-required", body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("raw token", json, StringComparison.Ordinal);
        await using var scope = app.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGarminConnectionRepository>();
        var saved = await repository.GetAsync(CancellationToken.None);
        Assert.Equal("reconnect-required", saved!.State);
    }

    [Fact]
    public async Task Disconnect_is_idempotent_and_always_deletes_when_challenge_clearing_fails()
    {
        var adapter = new FakeAdapterClient
        {
            ClearException = new GarminAdapterException(GarminAdapterError.AdapterUnavailable, "internal URL")
        };
        await using var app = CreateRiderApp(adapter);
        await SaveConnectionAsync(app.Services);
        using var client = app.CreateClient();

        using var first = await client.DeleteAsync("/api/garmin/connection");
        using var second = await client.DeleteAsync("/api/garmin/connection");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(2, adapter.ClearCalls);
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        Assert.Empty(await context.GarminConnections.ToListAsync());
    }

    [Fact]
    public async Task Disconnect_preserves_import_links_uploads_training_evidence_and_rider_model_history()
    {
        var adapter = new FakeAdapterClient();
        await using var app = CreateRiderApp(adapter);
        var evidence = await SeedImportedEvidenceAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.DeleteAsync("/api/garmin/connection");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        Assert.Empty(await context.GarminConnections.ToListAsync());
        Assert.Equal(evidence.ImportId, (await context.GarminActivityImports.SingleAsync()).GarminActivityId);
        Assert.Equal(evidence.UploadId, (await context.Uploads.SingleAsync()).Id);
        Assert.Equal(evidence.ActivityId, (await context.TrainingActivities.SingleAsync()).Id);
        Assert.Equal(evidence.ModelIds, await context.RiderModels.OrderBy(model => model.CreatedAt).Select(model => model.Id).ToListAsync());
    }

    [Fact]
    public void Authentication_request_contracts_redact_credentials_and_mfa_codes_from_ToString()
    {
        var contracts = typeof(ErrorCodes).Assembly;
        var loginType = contracts.GetType("RouteTimer.Contracts.Garmin.GarminLoginRequest");
        var mfaType = contracts.GetType("RouteTimer.Contracts.Garmin.GarminMfaRequest");

        Assert.NotNull(loginType);
        Assert.NotNull(mfaType);
        var login = Activator.CreateInstance(loginType, "rider@example.com", "top-secret-password")!;
        var mfa = Activator.CreateInstance(mfaType, "opaque-challenge", "739201")!;

        Assert.DoesNotContain("rider@example.com", login.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret-password", login.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("739201", mfa.ToString(), StringComparison.Ordinal);
    }

    private static RouteTimerApiFactory CreateRiderApp(FakeAdapterClient adapter) =>
        new RouteTimerApiFactory().WithRiderAuthentication(services =>
        {
            services.RemoveAll<IGarminAdapterClient>();
            services.AddSingleton<IGarminAdapterClient>(adapter);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        });

    private static async Task SaveConnectionAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var protector = scope.ServiceProvider.GetRequiredService<IGarminTokenProtector>();
        var repository = scope.ServiceProvider.GetRequiredService<IGarminConnectionRepository>();
        await repository.SaveAsync(
            new GarminConnectionRecord("connected", "42", "Jamie", protector.Protect("saved-token"), Now, Now),
            CancellationToken.None);
    }

    private static async Task<(string ImportId, Guid UploadId, Guid ActivityId, IReadOnlyList<Guid> ModelIds)> SeedImportedEvidenceAsync(
        IServiceProvider services)
    {
        await SaveConnectionAsync(services);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        const string importId = "987654321";
        var uploadId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var firstModelId = Guid.NewGuid();
        var secondModelId = Guid.NewGuid();
        context.Uploads.Add(new StoredUploadEntity
        {
            Id = uploadId,
            Kind = "fit",
            FileName = "garmin-ride.fit",
            Content = [1, 2, 3],
            Sha256 = Enumerable.Repeat((byte)7, 32).ToArray(),
            CreatedAt = Now.AddHours(-2)
        });
        context.GarminActivityImports.Add(new GarminActivityImportEntity
        {
            GarminActivityId = importId,
            UploadId = uploadId,
            ActivityName = "Morning ride",
            LinkedAt = Now.AddHours(-2)
        });
        context.TrainingActivities.Add(new TrainingActivityEntity
        {
            Id = activityId,
            UploadId = uploadId,
            Name = "Morning ride",
            SourceFileName = "garmin-ride.fit",
            StartedAt = Now.AddHours(-3),
            EndedAt = Now.AddHours(-2),
            MovingDurationSeconds = 3600,
            Eligibility = "Eligible",
            PositionCoverage = 1,
            ElevationCoverage = 1,
            SpeedCoverage = 1,
            PowerCoverage = 1,
            ExclusionCounts = new Dictionary<string, int>(),
            ReasonCodes = [],
            CreatedAt = Now.AddHours(-2)
        });
        context.RiderModels.AddRange(
            Model(firstModelId, Now.AddHours(-1)),
            Model(secondModelId, Now));
        await context.SaveChangesAsync();
        return (importId, uploadId, activityId, new[] { firstModelId, secondModelId });
    }

    private static RiderModelEntity Model(Guid id, DateTimeOffset createdAt) => new()
    {
        Id = id,
        CreatedAt = createdAt,
        ProfileRiderWeightKg = 75,
        ProfileBikeWeightKg = 10,
        AlgorithmVersion = "v1",
        DrivetrainEfficiency = 0.97,
        AirDensity = 1.225,
        Crr = 0.004,
        CdA = 0.3,
        GlobalTypicalWatts = 200,
        ValidationStatus = "InsufficientData"
    };

    private static void AssertTokenFree(JsonElement body)
    {
        var names = body.EnumerateObject().Select(property => property.Name).ToList();
        Assert.Equal(["state", "garminUserId", "displayName", "challengeId"], names);
        Assert.DoesNotContain(names, name => name.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("code", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAdapterClient : IGarminAdapterClient
    {
        public GarminAdapterLogin LoginResult { get; set; } = new("connected", null, "token-json", "42", "Jamie");
        public GarminAdapterLogin MfaResult { get; set; } = new("connected", null, "token-json", "42", "Jamie");
        public GarminAdapterSession ValidateResult { get; set; } = new("rotated-token", "42", "Jamie");
        public GarminAdapterException? LoginException { get; set; }
        public GarminAdapterException? MfaException { get; set; }
        public GarminAdapterException? ValidateException { get; set; }
        public Exception? ClearException { get; set; }
        public int LoginCalls { get; private set; }
        public int MfaCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public int ClearCalls { get; private set; }

        public Task<GarminAdapterLogin> LoginAsync(string email, string password, CancellationToken cancellationToken)
        {
            LoginCalls++;
            return LoginException is null ? Task.FromResult(LoginResult) : Task.FromException<GarminAdapterLogin>(LoginException);
        }

        public Task<GarminAdapterLogin> CompleteMfaAsync(string challengeId, string code, CancellationToken cancellationToken)
        {
            MfaCalls++;
            return MfaException is null ? Task.FromResult(MfaResult) : Task.FromException<GarminAdapterLogin>(MfaException);
        }

        public Task<GarminAdapterSession> ValidateAsync(string tokenJson, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            return ValidateException is null ? Task.FromResult(ValidateResult) : Task.FromException<GarminAdapterSession>(ValidateException);
        }

        public Task<GarminAdapterActivityPage> GetActivitiesAsync(string tokenJson, int offset, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GarminAdapterActivityResult> GetActivityAsync(string tokenJson, string activityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GarminAdapterFitDownload> DownloadFitAsync(string tokenJson, string activityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ClearChallengesAsync(CancellationToken cancellationToken)
        {
            ClearCalls++;
            return ClearException is null ? Task.CompletedTask : Task.FromException(ClearException);
        }

        public Task<GarminAdapterCourse> CreateCourseAsync(string tokenJson, GarminCourseRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
