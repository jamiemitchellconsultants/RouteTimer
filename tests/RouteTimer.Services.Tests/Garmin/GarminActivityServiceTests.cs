using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RouteTimer.Services.Garmin;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Tests.Garmin;

public sealed class GarminActivityServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 14, 0, 0, TimeSpan.Zero);

    // Break caught: RouteTimer could trust adapter filtering, lose optional metadata, or compare imported IDs loosely.
    [Fact]
    public async Task GetActivities_filters_exact_types_preserves_metadata_and_marks_imports_with_ordinal_matching()
    {
        var adapter = new FakeAdapterClient
        {
            Page = new GarminAdapterActivityPage(
                [
                    Activity("ride", "road-cycling", distance: 42195.5, duration: 3723.25, ascent: 612.5, power: 238.75),
                    Activity("run", "running"),
                    Activity("gravel", "gravel-cycling"),
                    Activity("ROAD", "Road-Cycling"),
                    Activity("similar", "road-cycling-indoor"),
                    Activity("unknown", "unknown")
                ],
                50,
                "rotated-token")
        };
        var connections = ConnectedRepository("saved-token");
        var imports = new FakeImportRepository { LinkedIds = new HashSet<string>(["gravel", "RIDE"], StringComparer.Ordinal) };
        var service = Service(adapter, connections, imports);

        var page = await service.GetActivitiesAsync(null, CancellationToken.None);

        Assert.Equal("NTA", page.NextCursor);
        Assert.Collection(
            page.Activities,
            ride =>
            {
                Assert.Equal("ride", ride.ActivityId);
                Assert.Equal("Safe ride", ride.Name);
                Assert.Equal(Now.AddHours(-1), ride.StartedAt);
                Assert.Equal("road-cycling", ride.ActivityType);
                Assert.Equal(42195.5, ride.DistanceMetres);
                Assert.Equal(3723.25, ride.DurationSeconds);
                Assert.Equal(612.5, ride.AscentMetres);
                Assert.Equal(238.75, ride.AveragePowerWatts);
                Assert.False(ride.AlreadyImported);
            },
            gravel =>
            {
                Assert.Equal("gravel", gravel.ActivityId);
                Assert.Equal("gravel-cycling", gravel.ActivityType);
                Assert.Null(gravel.DistanceMetres);
                Assert.Null(gravel.DurationSeconds);
                Assert.Null(gravel.AscentMetres);
                Assert.Null(gravel.AveragePowerWatts);
                Assert.True(gravel.AlreadyImported);
            });
        Assert.Equal(1, imports.QueryCalls);
        Assert.Equal(["ride", "gravel"], imports.LastActivityIds);
        Assert.Equal("saved-token", adapter.LastTokenJson);
        Assert.Equal(0, adapter.LastOffset);
        Assert.Equal("rotated-token", connections.CurrentTokenJson);
        Assert.Equal(CancellationToken.None, connections.LastSaveCancellationToken);
    }

    // Break caught: an empty allowed page could produce invalid SQL or skip the repository's safe empty-input path.
    [Fact]
    public async Task GetActivities_handles_a_page_with_no_allowed_rows()
    {
        var adapter = new FakeAdapterClient
        {
            Page = new GarminAdapterActivityPage([Activity("run", "running")], null, "saved-token")
        };
        var connections = ConnectedRepository("saved-token");
        var imports = new FakeImportRepository();
        var service = Service(adapter, connections, imports);

        var page = await service.GetActivitiesAsync(null, CancellationToken.None);

        Assert.Empty(page.Activities);
        Assert.Null(page.NextCursor);
        Assert.Equal(1, imports.QueryCalls);
        Assert.Empty(imports.LastActivityIds);
        Assert.Equal(0, connections.SaveCalls);
    }

    [Theory]
    [InlineData("MA", 0)]
    [InlineData("NTA", 50)]
    [InlineData("MTAwMDAwMDAw", 100000000)]
    public async Task GetActivities_accepts_canonical_base64url_decimal_cursors(string cursor, int expectedOffset)
    {
        var adapter = new FakeAdapterClient();
        var service = Service(adapter, ConnectedRepository("saved-token"), new FakeImportRepository());

        await service.GetActivitiesAsync(cursor, CancellationToken.None);

        Assert.Equal(expectedOffset, adapter.LastOffset);
    }

    [Theory]
    [InlineData(0, "MA")]
    [InlineData(50, "NTA")]
    [InlineData(100000000, "MTAwMDAwMDAw")]
    public async Task GetActivities_emits_canonical_unpadded_base64url_cursors(int nextOffset, string expectedCursor)
    {
        var adapter = new FakeAdapterClient
        {
            Page = new GarminAdapterActivityPage([], nextOffset, "saved-token")
        };
        var service = Service(adapter, ConnectedRepository("saved-token"), new FakeImportRepository());

        var page = await service.GetActivitiesAsync(null, CancellationToken.None);

        Assert.Equal(expectedCursor, page.NextCursor);
        Assert.DoesNotContain("=", page.NextCursor, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("MA==")]
    [InlineData("MDE")]
    [InlineData("LTE")]
    [InlineData("KzE")]
    [InlineData("MTAwMDAwMDAx")]
    [InlineData("MTAwMDAwMDAwMA")]
    [InlineData("8A")]
    [InlineData("MQA")]
    [InlineData("MR")]
    public async Task GetActivities_rejects_malformed_noncanonical_negative_and_overflow_cursors(string cursor)
    {
        var adapter = new FakeAdapterClient();
        var service = Service(adapter, ConnectedRepository("saved-token"), new FakeImportRepository());

        await Assert.ThrowsAsync<GarminCursorInvalidException>(
            () => service.GetActivitiesAsync(cursor, CancellationToken.None));

        Assert.Equal(0, adapter.ActivityCalls);
    }

    [Fact]
    public async Task GetActivities_requires_a_saved_connection_without_decrypting_or_calling_the_adapter()
    {
        var adapter = new FakeAdapterClient();
        var protector = new TrackingTokenProtector();
        var service = Service(adapter, new FakeConnectionRepository(protector), new FakeImportRepository(), protector);

        await Assert.ThrowsAsync<GarminConnectionRequiredException>(
            () => service.GetActivitiesAsync(null, CancellationToken.None));

        Assert.Equal(0, protector.UnprotectCalls);
        Assert.Equal(0, adapter.ActivityCalls);
    }

    [Fact]
    public async Task GetActivities_requires_reconnection_without_decrypting_or_calling_the_adapter()
    {
        var adapter = new FakeAdapterClient();
        var protector = new TrackingTokenProtector();
        var connections = new FakeConnectionRepository(protector)
        {
            Current = Connection("reconnect-required", protector.Protect("saved-token"))
        };
        var service = Service(adapter, connections, new FakeImportRepository(), protector);

        await Assert.ThrowsAsync<GarminReconnectRequiredException>(
            () => service.GetActivitiesAsync(null, CancellationToken.None));

        Assert.Equal(0, protector.UnprotectCalls);
        Assert.Equal(0, adapter.ActivityCalls);
    }

    // Break caught: a deterministic adapter authentication failure could leave unusable saved tokens marked connected.
    [Fact]
    public async Task GetActivities_marks_authentication_failure_reconnect_required_non_cancellably()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new FakeAdapterClient
        {
            ActivityException = new GarminAdapterException(GarminAdapterError.Authentication, "private token detail"),
            BeforeActivityOutcome = cancellation.Cancel
        };
        var connections = ConnectedRepository("saved-token");
        connections.RejectCancelledPersistenceTokens = true;
        var service = Service(adapter, connections, new FakeImportRepository());

        await Assert.ThrowsAsync<GarminReconnectRequiredException>(
            () => service.GetActivitiesAsync(null, cancellation.Token));

        Assert.Equal("reconnect-required", connections.Current!.State);
        Assert.Equal(CancellationToken.None, connections.LastSaveCancellationToken);
        Assert.Equal("saved-token", connections.CurrentTokenJson);
    }

    // Break caught: cancellation after an adapter success could discard its rotated token before persistence.
    [Fact]
    public async Task GetActivities_persists_rotation_non_cancellably_after_adapter_success()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new FakeAdapterClient
        {
            Page = new GarminAdapterActivityPage([Activity("ride", "road-cycling")], null, "rotated-token"),
            BeforeActivityOutcome = cancellation.Cancel
        };
        var connections = ConnectedRepository("saved-token");
        connections.RejectCancelledPersistenceTokens = true;
        var service = Service(adapter, connections, new FakeImportRepository());

        var page = await service.GetActivitiesAsync(null, cancellation.Token);

        Assert.Single(page.Activities);
        Assert.Equal(CancellationToken.None, connections.LastSaveCancellationToken);
        Assert.Equal("rotated-token", connections.CurrentTokenJson);
        Assert.Equal(Now, connections.Current!.LastValidatedAt);
        Assert.Equal(Now, connections.Current.UpdatedAt);
    }

    private static GarminActivityService Service(
        IGarminAdapterClient adapter,
        FakeConnectionRepository connections,
        IGarminActivityImportRepository imports,
        TrackingTokenProtector? protector = null,
        GarminOperationGate? gate = null) =>
        new(
            adapter,
            connections,
            imports,
            protector ?? connections.Protector,
            gate ?? new GarminOperationGate(),
            new RouteTimer.Services.Training.TrainingUploadService(new UnusedTrainingUploadRepository(), new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now),
            NullLogger<GarminActivityService>.Instance);

    private static FakeConnectionRepository ConnectedRepository(string tokenJson)
    {
        var protector = new TrackingTokenProtector();
        return new FakeConnectionRepository(protector)
        {
            Current = Connection("connected", protector.Protect(tokenJson))
        };
    }

    private static GarminConnectionRecord Connection(string state, ProtectedGarminToken token) =>
        new(state, "42", "Jamie", token, Now.AddHours(-1), Now.AddMinutes(-5));

    private static GarminAdapterActivity Activity(
        string id,
        string type,
        double? distance = null,
        double? duration = null,
        double? ascent = null,
        double? power = null) =>
        new(id, "Safe ride", Now.AddHours(-1), type, distance, duration, ascent, power);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TrackingTokenProtector : IGarminTokenProtector
    {
        public int UnprotectCalls { get; private set; }

        public ProtectedGarminToken Protect(string tokenJson) =>
            new(1, new byte[12], Encoding.UTF8.GetBytes(tokenJson), new byte[16]);

        public string Unprotect(ProtectedGarminToken protectedToken)
        {
            UnprotectCalls++;
            return Encoding.UTF8.GetString(protectedToken.Ciphertext);
        }
    }

    private sealed class FakeConnectionRepository(TrackingTokenProtector protector) : IGarminConnectionRepository
    {
        public TrackingTokenProtector Protector { get; } = protector;
        public GarminConnectionRecord? Current { get; set; }
        public bool RejectCancelledPersistenceTokens { get; set; }
        public int SaveCalls { get; private set; }
        public CancellationToken LastSaveCancellationToken { get; private set; }
        public string? CurrentTokenJson => Current is null ? null : Protector.Unprotect(Current.Token);

        public Task<GarminConnectionRecord?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        public Task SaveAsync(GarminConnectionRecord connection, CancellationToken cancellationToken)
        {
            SaveCalls++;
            LastSaveCancellationToken = cancellationToken;
            if (RejectCancelledPersistenceTokens && cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            Current = connection;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeImportRepository : IGarminActivityImportRepository
    {
        public IReadOnlySet<string> LinkedIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public int QueryCalls { get; private set; }
        public IReadOnlyList<string> LastActivityIds { get; private set; } = [];

        public Task<GarminActivityImportLink?> GetAsync(string activityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<string>> GetLinkedIdsAsync(
            IReadOnlyCollection<string> activityIds,
            CancellationToken cancellationToken)
        {
            QueryCalls++;
            LastActivityIds = activityIds.ToArray();
            return Task.FromResult(LinkedIds);
        }
    }

    private sealed class UnusedTrainingUploadRepository : ITrainingUploadRepository
    {
        public Task<TrainingUploadAcceptance> AcceptAsync(
            StoredUpload upload,
            DateTimeOffset now,
            GarminActivitySource? garminSource,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAdapterClient : IGarminAdapterClient
    {
        public GarminAdapterActivityPage Page { get; set; } = new([], null, "saved-token");
        public GarminAdapterException? ActivityException { get; set; }
        public Action? BeforeActivityOutcome { get; set; }
        public int ActivityCalls { get; private set; }
        public string? LastTokenJson { get; private set; }
        public int? LastOffset { get; private set; }

        public Task<GarminAdapterActivityPage> GetActivitiesAsync(
            string tokenJson,
            int offset,
            CancellationToken cancellationToken)
        {
            ActivityCalls++;
            LastTokenJson = tokenJson;
            LastOffset = offset;
            BeforeActivityOutcome?.Invoke();
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
