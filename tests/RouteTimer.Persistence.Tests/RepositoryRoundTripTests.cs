using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Entities;

namespace RouteTimer.Persistence.Tests;

public sealed class RepositoryRoundTripTests
{
    [Fact]
    public async Task Save_prediction_preserves_model_and_profile_snapshot()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new RouteTimerDbContext(options);
        var prediction = new PredictionEntity
        {
            Id = Guid.NewGuid(),
            ModelVersion = "model-1",
            RiderWeightKg = 75,
            BikeWeightKg = 10,
            CreatedAt = DateTimeOffset.UtcNow
        };

        context.Predictions.Add(prediction);
        await context.SaveChangesAsync();
        var loaded = await context.Predictions.SingleAsync();

        Assert.Equal("model-1", loaded.ModelVersion);
        Assert.Equal(75, loaded.RiderWeightKg);
    }

    [Fact]
    public async Task Save_upload_preserves_raw_bytes_and_content_hash()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var hash = Enumerable.Repeat((byte)7, 32).ToArray();
        context.Uploads.Add(new StoredUploadEntity { Id = Guid.NewGuid(), Kind = "fit", FileName = "ride.fit", Content = [1, 2, 3], Sha256 = hash, CreatedAt = DateTimeOffset.UtcNow });

        await context.SaveChangesAsync();
        var loaded = await context.Uploads.SingleAsync();

        Assert.Equal(hash, loaded.Sha256);
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.Content);
    }
}
