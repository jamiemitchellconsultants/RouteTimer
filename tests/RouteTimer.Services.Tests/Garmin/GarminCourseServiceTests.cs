using System.Text;
using RouteTimer.Domain.Models;
using RouteTimer.Services.Garmin;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Tests.Garmin;

public sealed class GarminCourseServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid PredictionId = Guid.NewGuid();

    [Fact]
    public async Task Requires_a_connected_garmin_account()
    {
        var service = CreateService(connection: null);

        await Assert.ThrowsAsync<GarminConnectionRequiredException>(
            () => service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Requires_reconnection_when_the_stored_connection_needs_it()
    {
        var service = CreateService(connection: Connected() with { State = "reconnect-required" });

        await Assert.ThrowsAsync<GarminReconnectRequiredException>(
            () => service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Refuses_a_prediction_that_has_no_route()
    {
        var predictions = new FakePredictionRepository(WithSegments: false);
        var service = CreateService(connection: Connected(), predictions: predictions);

        await Assert.ThrowsAsync<PredictionNotCompleteException>(
            () => service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Refuses_an_unknown_prediction()
    {
        var predictions = new FakePredictionRepository(WithSegments: true) { Source = null };
        var service = CreateService(connection: Connected(), predictions: predictions);

        await Assert.ThrowsAsync<PredictionMissingException>(
            () => service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Sends_the_untimed_variant_and_records_the_course_id()
    {
        var adapter = new FakeGarminAdapterClient
        {
            OnCreateCourseAsync = (_, request, _) =>
            {
                var gpx = Encoding.UTF8.GetString(request.Gpx);
                Assert.DoesNotContain("<time>", gpx.Split("<trkseg>")[1], StringComparison.Ordinal);
                return Task.FromResult(new GarminAdapterCourse(4242, "Kingston to Dorking", "refreshed-token"));
            }
        };
        var predictions = new FakePredictionRepository(WithSegments: true);
        var service = CreateService(connection: Connected(), adapter: adapter, predictions: predictions);

        var created = await service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None);

        Assert.Equal(4242, created.CourseId);
        Assert.Equal(4242, predictions.RecordedCourseId);
        Assert.Equal(Now, predictions.RecordedUploadedAt);
    }

    [Fact]
    public async Task Persists_the_refreshed_token()
    {
        var connections = new FakeConnectionRepository { Current = Connected() };
        var service = CreateService(connection: Connected(), connections: connections);

        await service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None);

        Assert.Equal("refreshed-token", connections.CurrentTokenJson);
    }

    [Fact]
    public async Task Defaults_the_activity_type_to_road_cycling()
    {
        var adapter = new FakeGarminAdapterClient
        {
            OnCreateCourseAsync = (_, request, _) =>
            {
                Assert.Equal("road_biking", request.ActivityType);
                return Task.FromResult(new GarminAdapterCourse(1, "R", "refreshed-token"));
            }
        };
        var service = CreateService(connection: Connected(), adapter: adapter);

        await service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None);
    }

    [Fact]
    public async Task Sends_computed_elevation_totals()
    {
        var adapter = new FakeGarminAdapterClient
        {
            OnCreateCourseAsync = (_, request, _) =>
            {
                Assert.Equal(15, request.ElevationGainMetres);
                Assert.Equal(5, request.ElevationLossMetres);
                return Task.FromResult(new GarminAdapterCourse(1, "R", "refreshed-token"));
            }
        };
        var service = CreateService(connection: Connected(), adapter: adapter);

        await service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None);
    }

    private static GarminCourseService CreateService(
        GarminConnectionRecord? connection,
        FakeGarminAdapterClient? adapter = null,
        FakeConnectionRepository? connections = null,
        FakePredictionRepository? predictions = null) =>
        new(
            adapter ?? new FakeGarminAdapterClient(),
            connections ?? new FakeConnectionRepository { Current = connection },
            predictions ?? new FakePredictionRepository(WithSegments: true),
            new TrackingTokenProtector(),
            new GarminOperationGate(),
            new FixedTimeProvider(Now));

    private static GarminConnectionRecord Connected() =>
        new("connected", "42", "Jamie", new ProtectedGarminToken(1, new byte[12], Encoding.UTF8.GetBytes("token-json"), new byte[16]), Now.AddHours(-1), Now.AddMinutes(-5));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TrackingTokenProtector : IGarminTokenProtector
    {
        public ProtectedGarminToken Protect(string tokenJson) =>
            new(1, new byte[12], Encoding.UTF8.GetBytes(tokenJson), new byte[16]);

        public string Unprotect(ProtectedGarminToken protectedToken) =>
            Encoding.UTF8.GetString(protectedToken.Ciphertext);
    }

    private sealed class FakeConnectionRepository : IGarminConnectionRepository
    {
        public GarminConnectionRecord? Current { get; set; }
        public string? CurrentTokenJson => Current is null ? null : Encoding.UTF8.GetString(Current.Token.Ciphertext);

        public Task<GarminConnectionRecord?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        public Task SaveAsync(GarminConnectionRecord connection, CancellationToken cancellationToken)
        {
            Current = connection;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePredictionRepository(bool WithSegments) : IPredictionRepository
    {
        public PredictionGpxSource? Source { get; set; } = WithSegments
            ? new PredictionGpxSource(
                "Kingston to Dorking",
                "Predicted route",
                Now,
                Now.AddHours(-1),
                [
                    new PersistedPredictionSegment(0, 51.4085, -0.3064, 100, 0, 0, 0, 0, 200, 8, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(0), ConfidenceLevel.High),
                    new PersistedPredictionSegment(1, 51.4090, -0.3070, 115, 500, 500, 0, 0, 200, 8, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), ConfidenceLevel.High),
                    new PersistedPredictionSegment(2, 51.4095, -0.3080, 110, 1000, 500, 0, 0, 200, 8, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120), ConfidenceLevel.High)
                ])
            : new PredictionGpxSource("Empty", "Empty", Now, Now, []);

        public long? RecordedCourseId { get; private set; }
        public DateTimeOffset? RecordedUploadedAt { get; private set; }

        public Task<QueuedPredictionSubmission> CreateQueuedAsync(QueuedPredictionCreation creation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PredictionForProcessing?> GetForProcessingAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryPublishAsync(Guid predictionId, Guid jobId, string workerId, PredictionPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid predictionId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailAsync(Guid predictionId, string code, string message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PredictionSummary>> GetSummariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PredictionDetail?> GetAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PredictionGpxSource?> GetGpxSourceAsync(Guid predictionId, CancellationToken cancellationToken) => Task.FromResult(Source);

        public Task RecordGarminCourseAsync(Guid predictionId, long courseId, DateTimeOffset uploadedAt, CancellationToken cancellationToken)
        {
            RecordedCourseId = courseId;
            RecordedUploadedAt = uploadedAt;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGarminAdapterClient : IGarminAdapterClient
    {
        public Func<string, GarminCourseRequest, CancellationToken, Task<GarminAdapterCourse>>? OnCreateCourseAsync { get; set; }

        public Task<GarminAdapterCourse> CreateCourseAsync(string tokenJson, GarminCourseRequest request, CancellationToken cancellationToken) =>
            OnCreateCourseAsync is not null
                ? OnCreateCourseAsync(tokenJson, request, cancellationToken)
                : Task.FromResult(new GarminAdapterCourse(1, "R", "refreshed-token"));

        public Task<GarminAdapterLogin> LoginAsync(string email, string password, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GarminAdapterLogin> CompleteMfaAsync(string challengeId, string code, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GarminAdapterSession> ValidateAsync(string tokenJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GarminAdapterActivityPage> GetActivitiesAsync(string tokenJson, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GarminAdapterActivityResult> GetActivityAsync(string tokenJson, string activityId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GarminAdapterFitDownload> DownloadFitAsync(string tokenJson, string activityId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearChallengesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
