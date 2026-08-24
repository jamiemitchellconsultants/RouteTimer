using RouteTimer.Services.Training;

namespace RouteTimer.Services.Tests.Training;

public sealed class TrainingUploadServiceTests
{
    [Fact]
    public async Task Accept_batch_returns_independent_accepted_duplicate_and_invalid_results()
    {
        var service = new TrainingUploadService();
        var uploads = new[]
        {
            new TrainingUpload("one.fit", [1, 2, 3]),
            new TrainingUpload("copy.fit", [1, 2, 3]),
            new TrainingUpload("broken.txt", [9])
        };

        var results = await service.AcceptAsync(uploads, CancellationToken.None);

        Assert.Collection(results,
            result => Assert.Equal(UploadOutcome.Accepted, result.Outcome),
            result => Assert.Equal(UploadOutcome.Duplicate, result.Outcome),
            result => Assert.Equal(UploadOutcome.Invalid, result.Outcome));
    }
}
