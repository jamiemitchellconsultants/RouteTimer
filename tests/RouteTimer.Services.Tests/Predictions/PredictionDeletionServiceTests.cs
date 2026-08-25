using RouteTimer.Services.Persistence;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Tests.Predictions;

public sealed class PredictionDeletionServiceTests
{
    [Fact]
    public async Task Delete_passes_the_time_provider_timestamp_and_id_to_repository()
    {
        var now = new DateTimeOffset(2026, 8, 25, 15, 0, 0, TimeSpan.Zero);
        var repository = new FakeRepository(true);
        var service = new PredictionDeletionService(repository, new FixedTimeProvider(now));
        var id = Guid.NewGuid();

        Assert.True(await service.DeleteAsync(id, CancellationToken.None));
        Assert.Equal((id, now), repository.Request);
    }

    [Fact]
    public async Task Delete_returns_repository_result()
    {
        var repository = new FakeRepository(false);
        var service = new PredictionDeletionService(repository, new FixedTimeProvider(DateTimeOffset.UtcNow));

        Assert.False(await service.DeleteAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeRepository(bool result) : IPredictionRepository
    {
        public (Guid Id, DateTimeOffset Now)? Request { get; private set; }

        public Task<QueuedPredictionSubmission> CreateQueuedAsync(QueuedPredictionCreation creation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PredictionForProcessing?> GetForProcessingAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryPublishAsync(Guid predictionId, Guid jobId, string workerId, PredictionPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid predictionId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Request = (predictionId, now);
            return Task.FromResult(result);
        }
        public Task FailAsync(Guid predictionId, string code, string message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PredictionSummary>> GetSummariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PredictionDetail?> GetAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
