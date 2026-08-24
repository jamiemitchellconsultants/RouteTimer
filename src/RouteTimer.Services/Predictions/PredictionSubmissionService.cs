using System.Security.Cryptography;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Predictions;

public sealed class PredictionSubmissionService(
    IProfileRepository profiles,
    IRiderModelRepository models,
    IPredictionRepository predictions,
    TimeProvider timeProvider)
{
    private const int MaximumBytes = 50 * 1024 * 1024;

    public async Task<QueuedPredictionSubmission> SubmitAsync(PredictionUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (!upload.FileName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase))
        {
            throw new PredictionSubmissionException("prediction-gpx-required", "A .gpx route upload is required.");
        }

        var profile = await profiles.GetAsync(cancellationToken)
            ?? throw new PredictionSubmissionException("profile-required", "A rider profile is required before predicting a route.");
        var model = await models.GetCurrentAsync(cancellationToken)
            ?? throw new PredictionSubmissionException("model-not-ready", "A rider model is required before predicting a route.");
        var content = await ReadBoundedAsync(upload.Content, cancellationToken);
        if (content.Length == 0)
        {
            throw new PredictionSubmissionException("invalid-gpx-upload", "The GPX upload is empty.");
        }

        var now = timeProvider.GetUtcNow();
        var stored = new StoredUpload(Guid.NewGuid(), upload.FileName, "gpx", content, SHA256.HashData(content), now);
        return await predictions.CreateQueuedAsync(new QueuedPredictionCreation(stored, model, profile, PredictionAssumptions.RoadCalmDryMovingOnly, now), cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(bytes, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumBytes)
            {
                throw new PredictionSubmissionException("gpx-too-large", "The GPX upload exceeds 50 MB.");
            }

            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }
}
