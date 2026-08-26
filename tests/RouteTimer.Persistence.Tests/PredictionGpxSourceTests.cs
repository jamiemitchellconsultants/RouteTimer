using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;
using RouteTimer.Persistence.Repositories;

namespace RouteTimer.Persistence.Tests;

public sealed class PredictionGpxSourceTests
{
    [Fact]
    public async Task Reads_a_gpx_source_carrying_the_upload_name_and_segments()
    {
        await using var context = CreateContext();
        var predictionId = Guid.NewGuid();
        context.Uploads.Add(new StoredUploadEntity
        {
            Id = Guid.NewGuid(),
            Kind = "gpx",
            FileName = "kingston-dorking.gpx",
            Content = [1, 2, 3],
            Sha256 = [4, 5, 6],
            CreatedAt = DateTimeOffset.UnixEpoch
        });
        await context.SaveChangesAsync();
        var uploadId = context.Uploads.Single().Id;

        context.Predictions.Add(new PredictionEntity
        {
            Id = predictionId,
            UploadId = uploadId,
            RiderModelId = Guid.NewGuid(),
            ModelVersion = "1.4.0",
            State = "Succeeded",
            DistanceMetres = 34200,
            AscentMetres = 410,
            MovingSeconds = 4350,
            AverageSpeedMetresPerSecond = 7.86,
            AveragePowerWatts = 214,
            Confidence = "High",
            CreatedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch.AddHours(1),
            Segments =
            [
                new PredictionSegmentEntity
                {
                    PredictionId = predictionId,
                    Sequence = 0,
                    Latitude = 51.4085,
                    Longitude = -0.3064,
                    ElevationMetres = 12.4,
                    Confidence = "High"
                },
                new PredictionSegmentEntity
                {
                    PredictionId = predictionId,
                    Sequence = 1,
                    Latitude = 51.4090,
                    Longitude = -0.3070,
                    ElevationMetres = 15.0,
                    CumulativeMovingSeconds = 30,
                    Confidence = "High"
                }
            ]
        });
        await context.SaveChangesAsync();

        var repository = new PredictionRepository(context);
        var source = await repository.GetGpxSourceAsync(predictionId, CancellationToken.None);

        Assert.NotNull(source);
        Assert.Equal("kingston-dorking", source.RouteName);
        Assert.Equal(2, source.Segments.Count);
        Assert.Contains("Predicted", source.Description, StringComparison.Ordinal);
        Assert.Contains("1.4.0", source.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Returns_null_for_an_unknown_prediction()
    {
        await using var context = CreateContext();
        var repository = new PredictionRepository(context);

        Assert.Null(await repository.GetGpxSourceAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private static RouteTimerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new RouteTimerDbContext(options);
    }
}
