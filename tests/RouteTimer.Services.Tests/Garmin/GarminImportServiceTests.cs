using System.Text;
using RouteTimer.Services.Garmin;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Training;

namespace RouteTimer.Services.Tests.Garmin;

public sealed class GarminImportServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 16, 0, 0, TimeSpan.Zero);

    // Break caught: one failed Garmin download aborts the batch or lets later items overtake earlier ones.
    [Fact]
    public async Task ImportAsync_continues_after_one_download_failure_and_preserves_input_order()
    {
        var adapter = new FakeAdapterClient();
        adapter.AddActivity("1", "Road ride", "road-cycling");
        adapter.AddActivity("2", "Gravel ride", "gravel-cycling");
        adapter.DownloadFailures["1"] = new GarminAdapterException(GarminAdapterError.Unavailable, "private detail");
        adapter.AddDownload("2", [2, 3, 4]);
        var service = Service(adapter);

        var results = await service.ImportAsync(["1", "2"], CancellationToken.None);

        Assert.Equal(["1", "2"], results.Select(result => result.ActivityId));
        Assert.Equal("download-failed", results[0].Outcome);
        Assert.Equal("garmin-unavailable", results[0].ErrorCode);
        Assert.Equal("accepted", results[1].Outcome);
        Assert.Equal(["summary:1", "download:1", "summary:2", "download:2"], adapter.Operations);
    }

    // Break caught: an empty, oversized, or repeated selection reaches Garmin instead of a stable request-level rejection.
    [Theory]
    [MemberData(nameof(InvalidSelections))]
    public async Task ImportAsync_rejects_invalid_selection_structure_before_any_adapter_call(string[] activityIds)
    {
        var adapter = new FakeAdapterClient();
        var service = Service(adapter);

        await Assert.ThrowsAsync<GarminImportLimitException>(
            () => service.ImportAsync(activityIds, CancellationToken.None));

        Assert.Empty(adapter.Operations);
    }

    public static TheoryData<string[]> InvalidSelections => new()
    {
        Array.Empty<string>(),
        Enumerable.Range(1, 11).Select(value => value.ToString()).ToArray(),
        new[] { "1", "1" }
    };

    // Break caught: already-linked activities are downloaded again or returned without their original parse identifiers.
    [Fact]
    public async Task ImportAsync_short_circuits_an_existing_link_without_contacting_the_adapter()
    {
        var adapter = new FakeAdapterClient();
        var uploadId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var imports = new FakeImportRepository
        {
            Link = new GarminActivityImportLink("123", "Already there", uploadId, jobId)
        };
        var service = Service(adapter, imports: imports);

        var result = Assert.Single(await service.ImportAsync(["123"], CancellationToken.None));

        Assert.Equal(new GarminImportResult("123", "Already there", "already-imported", uploadId, jobId, null), result);
        Assert.Empty(adapter.Operations);
    }

    // Break caught: a selected ID can be used as an arbitrary download proxy when Garmin returns another ID or disallowed type.
    [Theory]
    [InlineData("different", "road-cycling")]
    [InlineData("123", "indoor-cycling")]
    [InlineData("123", "Road-Cycling")]
    public async Task ImportAsync_revalidates_the_exact_summary_id_and_allowed_type(string returnedId, string activityType)
    {
        var adapter = new FakeAdapterClient();
        adapter.ActivityResults["123"] = new GarminAdapterActivityResult(
            Activity(returnedId, "Unsafe selection", activityType),
            "summary-token");
        var service = Service(adapter);

        var result = Assert.Single(await service.ImportAsync(["123"], CancellationToken.None));

        Assert.Equal("invalid-fit", result.Outcome);
        Assert.Equal("garmin-response-invalid", result.ErrorCode);
        Assert.Null(result.UploadId);
        Assert.Null(result.JobId);
        Assert.Equal(["summary:123"], adapter.Operations);
    }

    // Break caught: an exact but oversized adapter ID reaches the 64-character database column and aborts the batch.
    [Fact]
    public async Task ImportAsync_rejects_an_oversized_summary_id_before_download()
    {
        var activityId = new string('1', 65);
        var adapter = new FakeAdapterClient();
        adapter.AddActivity(activityId, "Road ride", "road-cycling");
        var service = Service(adapter);

        var result = Assert.Single(await service.ImportAsync([activityId], CancellationToken.None));

        Assert.Equal("invalid-fit", result.Outcome);
        Assert.Equal("garmin-response-invalid", result.ErrorCode);
        Assert.Equal([$"summary:{activityId}"], adapter.Operations);
    }

    // Break caught: cancellation after a committed first item starts or rolls back later selections.
    [Fact]
    public async Task ImportAsync_cancellation_after_first_acceptance_preserves_it_and_stops_before_the_next_item()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new FakeAdapterClient();
        adapter.AddActivity("1", "First", "road-cycling");
        adapter.AddActivity("2", "Second", "gravel-cycling");
        adapter.AddDownload("1", [1]);
        adapter.AddDownload("2", [2]);
        var uploadRepository = new FakeTrainingUploadRepository { AfterAcceptance = cancellation.Cancel };
        var service = Service(adapter, uploadRepository: uploadRepository);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ImportAsync(["1", "2"], cancellation.Token));

        Assert.Single(uploadRepository.Accepted);
        Assert.Equal("1", uploadRepository.Accepted[0].Source!.ActivityId);
        Assert.Equal(["summary:1", "download:1"], adapter.Operations);
    }

    // Break caught: adapter names or IDs can create path traversal/control characters or exceed the upload filename limit.
    [Fact]
    public async Task ImportAsync_sanitizes_and_bounds_the_deterministic_filename_and_disposes_the_fit()
    {
        var adapter = new FakeAdapterClient();
        var longUnsafeName = "../" + new string('x', 600) + "/Morning\u0001 Ride";
        adapter.AddActivity("12/3", longUnsafeName, "road-cycling");
        var stream = adapter.AddDownload("12/3", [1, 2, 3]);
        var uploadRepository = new FakeTrainingUploadRepository();
        var service = Service(adapter, uploadRepository: uploadRepository);

        var result = Assert.Single(await service.ImportAsync(["12/3"], CancellationToken.None));

        Assert.Equal("accepted", result.Outcome);
        var accepted = Assert.Single(uploadRepository.Accepted);
        Assert.EndsWith("-123.fit", accepted.Upload.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain('/', accepted.Upload.FileName);
        Assert.DoesNotContain('\\', accepted.Upload.FileName);
        Assert.DoesNotContain(accepted.Upload.FileName, char.IsControl);
        Assert.InRange(accepted.Upload.FileName.Length, 1, 512);
        Assert.True(stream.IsDisposed);
    }

    // Break caught: successful summary/download responses rotate tokens that are lost when persistence observes request cancellation.
    [Fact]
    public async Task ImportAsync_persists_each_successful_token_rotation_non_cancellably_and_uses_it_for_the_next_call()
    {
        var adapter = new FakeAdapterClient();
        adapter.ActivityResults["1"] = new GarminAdapterActivityResult(
            Activity("1", "Road ride", "road-cycling"),
            "summary-token");
        adapter.AddDownload("1", [1, 2, 3], "download-token");
        var connections = ConnectedRepository("saved-token");
        connections.RejectCancelledPersistenceTokens = true;
        var service = Service(adapter, connections: connections);

        var result = Assert.Single(await service.ImportAsync(["1"], CancellationToken.None));

        Assert.Equal("accepted", result.Outcome);
        Assert.Equal(["summary-token", "download-token"], connections.SavedTokenJson);
        Assert.All(connections.SaveTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.Equal(["saved-token", "summary-token"], adapter.ReceivedTokens);
    }

    // Break caught: a successfully acquired FIT leaks when its response token is rejected before upload acceptance.
    [Fact]
    public async Task ImportAsync_disposes_the_download_when_its_returned_token_is_invalid()
    {
        var adapter = new FakeAdapterClient();
        adapter.AddActivity("1", "Road ride", "road-cycling");
        var stream = adapter.AddDownload("1", [1, 2, 3], " ");
        var service = Service(adapter);

        var result = Assert.Single(await service.ImportAsync(["1"], CancellationToken.None));

        Assert.Equal("invalid-fit", result.Outcome);
        Assert.Equal("garmin-response-invalid", result.ErrorCode);
        Assert.True(stream.IsDisposed);
    }

    // Break caught: a duplicate FIT is reported as already imported or loses the original upload/job identifiers.
    [Fact]
    public async Task ImportAsync_maps_hash_duplicate_with_existing_identifiers()
    {
        var uploadId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var adapter = new FakeAdapterClient();
        adapter.AddActivity("1", "Road ride", "road-cycling");
        adapter.AddDownload("1", [1, 2, 3]);
        var uploads = new FakeTrainingUploadRepository
        {
            Acceptance = new TrainingUploadAcceptance(
                TrainingUploadAcceptanceOutcome.DuplicateHash,
                uploadId,
                jobId)
        };
        var service = Service(adapter, uploadRepository: uploads);

        var result = Assert.Single(await service.ImportAsync(["1"], CancellationToken.None));

        Assert.Equal(new GarminImportResult("1", "Road ride", "duplicate", uploadId, jobId, "duplicate-upload"), result);
    }

    private static GarminActivityService Service(
        FakeAdapterClient adapter,
        FakeConnectionRepository? connections = null,
        FakeImportRepository? imports = null,
        FakeTrainingUploadRepository? uploadRepository = null)
    {
        connections ??= ConnectedRepository("saved-token");
        uploadRepository ??= new FakeTrainingUploadRepository();
        return new GarminActivityService(
            adapter,
            connections,
            imports ?? new FakeImportRepository(),
            connections.Protector,
            new GarminOperationGate(),
            new TrainingUploadService(uploadRepository, new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now));
    }

    private static FakeConnectionRepository ConnectedRepository(string tokenJson)
    {
        var protector = new TrackingTokenProtector();
        return new FakeConnectionRepository(protector)
        {
            Current = new GarminConnectionRecord(
                "connected", "42", "Jamie", protector.Protect(tokenJson), Now.AddHours(-1), Now.AddMinutes(-5))
        };
    }

    private static GarminAdapterActivity Activity(string id, string name, string type) =>
        new(id, name, Now.AddHours(-1), type, null, null, null, null);

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

    private sealed class FakeConnectionRepository(TrackingTokenProtector protector) : IGarminConnectionRepository
    {
        public TrackingTokenProtector Protector { get; } = protector;
        public GarminConnectionRecord? Current { get; set; }
        public bool RejectCancelledPersistenceTokens { get; set; }
        public List<string> SavedTokenJson { get; } = [];
        public List<CancellationToken> SaveTokens { get; } = [];

        public Task<GarminConnectionRecord?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        public Task SaveAsync(GarminConnectionRecord connection, CancellationToken cancellationToken)
        {
            if (RejectCancelledPersistenceTokens && cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            Current = connection;
            SavedTokenJson.Add(Protector.Unprotect(connection.Token));
            SaveTokens.Add(cancellationToken);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeImportRepository : IGarminActivityImportRepository
    {
        public GarminActivityImportLink? Link { get; set; }

        public Task<GarminActivityImportLink?> GetAsync(string activityId, CancellationToken cancellationToken) =>
            Task.FromResult(Link?.ActivityId == activityId ? Link : null);

        public Task<IReadOnlySet<string>> GetLinkedIdsAsync(
            IReadOnlyCollection<string> activityIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed class FakeTrainingUploadRepository : ITrainingUploadRepository
    {
        public List<(StoredUpload Upload, GarminActivitySource? Source)> Accepted { get; } = [];
        public Action? AfterAcceptance { get; set; }
        public TrainingUploadAcceptance? Acceptance { get; set; }

        public Task<TrainingUploadAcceptance> AcceptAsync(
            StoredUpload upload,
            DateTimeOffset now,
            GarminActivitySource? garminSource,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Accepted.Add((upload, garminSource));
            var result = Acceptance ?? new TrainingUploadAcceptance(
                TrainingUploadAcceptanceOutcome.Accepted,
                upload.Id,
                Guid.NewGuid());
            AfterAcceptance?.Invoke();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeAdapterClient : IGarminAdapterClient
    {
        public Dictionary<string, GarminAdapterActivityResult> ActivityResults { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, GarminAdapterException> ActivityFailures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, GarminAdapterFitDownload> Downloads { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, GarminAdapterException> DownloadFailures { get; } = new(StringComparer.Ordinal);
        public List<string> Operations { get; } = [];
        public List<string> ReceivedTokens { get; } = [];

        public void AddActivity(string id, string name, string type, string tokenJson = "saved-token") =>
            ActivityResults[id] = new GarminAdapterActivityResult(Activity(id, name, type), tokenJson);

        public TrackingStream AddDownload(string id, byte[] content, string tokenJson = "saved-token")
        {
            var stream = new TrackingStream(content);
            Downloads[id] = new GarminAdapterFitDownload("adapter-name.fit", stream, tokenJson);
            return stream;
        }

        public Task<GarminAdapterActivityResult> GetActivityAsync(
            string tokenJson,
            string activityId,
            CancellationToken cancellationToken)
        {
            Operations.Add($"summary:{activityId}");
            ReceivedTokens.Add(tokenJson);
            return ActivityFailures.TryGetValue(activityId, out var failure)
                ? Task.FromException<GarminAdapterActivityResult>(failure)
                : Task.FromResult(ActivityResults[activityId]);
        }

        public Task<GarminAdapterFitDownload> DownloadFitAsync(
            string tokenJson,
            string activityId,
            CancellationToken cancellationToken)
        {
            Operations.Add($"download:{activityId}");
            ReceivedTokens.Add(tokenJson);
            return DownloadFailures.TryGetValue(activityId, out var failure)
                ? Task.FromException<GarminAdapterFitDownload>(failure)
                : Task.FromResult(Downloads[activityId]);
        }

        public Task<GarminAdapterLogin> LoginAsync(string email, string password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GarminAdapterLogin> CompleteMfaAsync(string challengeId, string code, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GarminAdapterSession> ValidateAsync(string tokenJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GarminAdapterActivityPage> GetActivitiesAsync(string tokenJson, int offset, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ClearChallengesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TrackingStream(byte[] content) : MemoryStream(content)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            await base.DisposeAsync();
        }
    }
}
