using System.Text;
using Microsoft.Extensions.Time.Testing;
using RouteTimer.Domain.Models;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.RoutePacer;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Tests.RoutePacer;

public sealed class RoutePacerHandoffServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
    private static readonly Uri BaseUrl = new("https://pacetracking.tqaentry.com");
    private static readonly Uri PayloadUrl = new("https://pacetracking.tqaentry.com/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    private static readonly Guid PredictionId = Guid.Parse("2f1a5b7c-0d3e-4f10-9a2b-3c4d5e6f7a8b");

    [Fact]
    public async Task Create_uploads_the_exact_timed_GPX_and_signs_the_validated_grant()
    {
        var predictions = new FakePredictionRepository();
        var relay = new FakeRelayClient(new RoutePacerRelayGrant(PayloadUrl, Now.AddMinutes(10)));
        var signer = new RecordingSigner();

        var handoff = await Service(predictions, relay, signer).CreateAsync(PredictionId, CancellationToken.None);

        // The timed variant, byte-identical to what the download endpoint would produce: a track
        // without <time> would import into PaceTracker as a route with no pacing at all.
        var expected = Encoding.UTF8.GetBytes(PredictionGpxWriter.Write(predictions.Source!, timed: true));
        Assert.Equal(expected, relay.Uploaded);
        Assert.Contains("<time>", Encoding.UTF8.GetString(relay.Uploaded!).Split("<trkseg>")[1], StringComparison.Ordinal);

        Assert.Equal(
            $"rt\n1\n{PayloadUrl.AbsoluteUri}\nKingston to Dorking\n{Now.ToUnixTimeMilliseconds()}",
            signer.SignedCanonical);

        Assert.StartsWith("https://pacetracking.tqaentry.com/open?", handoff.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("sig=test-signature", handoff.Url.AbsoluteUri, StringComparison.Ordinal);
        // The relay owns the lifetime; the service reports it unchanged rather than recomputing it.
        Assert.Equal(Now.AddMinutes(10), handoff.ExpiresAt);
    }

    [Fact]
    public async Task Disabled_handoff_does_not_read_the_prediction_or_call_the_relay()
    {
        var predictions = new FakePredictionRepository();
        var relay = new FakeRelayClient();
        var signer = new RecordingSigner();

        await Assert.ThrowsAsync<RoutePacerHandoffDisabledException>(
            () => Service(predictions, relay, signer, enabled: false).CreateAsync(PredictionId, CancellationToken.None));

        Assert.Equal(0, predictions.Reads);
        Assert.Null(relay.Uploaded);
        Assert.Null(signer.SignedCanonical);
    }

    [Fact]
    public async Task Missing_prediction_does_not_call_the_relay()
    {
        var predictions = new FakePredictionRepository { Source = null };
        var relay = new FakeRelayClient();
        var signer = new RecordingSigner();

        await Assert.ThrowsAsync<RoutePacerPredictionMissingException>(
            () => Service(predictions, relay, signer).CreateAsync(PredictionId, CancellationToken.None));

        Assert.Null(relay.Uploaded);
        Assert.Null(signer.SignedCanonical);
    }

    // The existing incomplete signal is reused rather than replaced, so the endpoint keeps
    // returning the same 409 the GPX download and Garmin push already return.
    [Fact]
    public async Task Segment_free_prediction_does_not_call_the_relay()
    {
        var predictions = new FakePredictionRepository
        {
            Source = new PredictionGpxSource("Empty", "Empty", Now, Now, [])
        };
        var relay = new FakeRelayClient();
        var signer = new RecordingSigner();

        await Assert.ThrowsAsync<PredictionNotCompleteException>(
            () => Service(predictions, relay, signer).CreateAsync(PredictionId, CancellationToken.None));

        Assert.Null(relay.Uploaded);
        Assert.Null(signer.SignedCanonical);
    }

    [Fact]
    public async Task Relay_failure_does_not_attempt_to_sign()
    {
        var relay = new FakeRelayClient
        {
            Failure = new RoutePacerRelayException(RoutePacerRelayFailure.Unavailable, "unavailable")
        };
        var signer = new RecordingSigner();

        await Assert.ThrowsAsync<RoutePacerRelayException>(
            () => Service(new FakePredictionRepository(), relay, signer).CreateAsync(PredictionId, CancellationToken.None));

        Assert.Null(signer.SignedCanonical);
    }

    [Fact]
    public async Task Create_passes_the_caller_cancellation_token_through()
    {
        using var cancellation = new CancellationTokenSource();
        var predictions = new FakePredictionRepository();
        var relay = new FakeRelayClient(new RoutePacerRelayGrant(PayloadUrl, Now.AddMinutes(10)));

        await Service(predictions, relay, new RecordingSigner()).CreateAsync(PredictionId, cancellation.Token);

        Assert.Equal(cancellation.Token, predictions.LastToken);
        Assert.Equal(cancellation.Token, relay.LastToken);
    }

    private static RoutePacerHandoffService Service(
        FakePredictionRepository predictions,
        FakeRelayClient relay,
        IRoutePacerInvocationSigner signer,
        bool enabled = true) =>
        new(
            new PredictionQueryService(predictions),
            relay,
            signer,
            new RoutePacerHandoffConfiguration(enabled, BaseUrl),
            new FakeTimeProvider(Now));

    private sealed class RecordingSigner : IRoutePacerInvocationSigner
    {
        public string? SignedCanonical { get; private set; }

        public string Sign(ReadOnlySpan<byte> canonicalBytes)
        {
            SignedCanonical = Encoding.UTF8.GetString(canonicalBytes);
            return "test-signature";
        }
    }

    private sealed class FakeRelayClient(RoutePacerRelayGrant? grant = null) : IRoutePacerRelayClient
    {
        public byte[]? Uploaded { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public RoutePacerRelayException? Failure { get; init; }

        public Task<RoutePacerRelayGrant> UploadAsync(byte[] timedGpx, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            if (Failure is not null)
            {
                throw Failure;
            }

            Uploaded = timedGpx;
            return Task.FromResult(grant ?? throw new InvalidOperationException("No grant configured."));
        }
    }

    private sealed class FakePredictionRepository : IPredictionRepository
    {
        public int Reads { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public PredictionGpxSource? Source { get; set; } = new(
            "Kingston to Dorking",
            "Predicted route",
            Now,
            Now.AddHours(-1),
            [
                new PersistedPredictionSegment(0, 51.4085, -0.3064, 100, 0, 0, 0, 0, 200, 8, TimeSpan.FromSeconds(10), TimeSpan.Zero, ConfidenceLevel.High),
                new PersistedPredictionSegment(1, 51.4090, -0.3070, 115, 500, 500, 0, 0, 200, 8, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), ConfidenceLevel.High)
            ]);

        public Task<PredictionGpxSource?> GetGpxSourceAsync(Guid predictionId, CancellationToken cancellationToken)
        {
            Reads++;
            LastToken = cancellationToken;
            return Task.FromResult(Source);
        }

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
