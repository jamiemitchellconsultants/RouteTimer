using RouteTimer.Domain.Jobs;

namespace RouteTimer.Services.Jobs;

public sealed class JobProgressReporter(IJobQueue jobs, TimeProvider timeProvider) : IJobProgressReporter
{
    public async Task ReportAsync(AnalysisJob job, int progressPercent, string stage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateProgress(progressPercent, stage);

        if (job.WorkerId is null)
        {
            throw new InvalidOperationException("A claimed job is required.");
        }

        if (!await jobs.ReportProgressAsync(job.Id, job.WorkerId, progressPercent, stage, timeProvider.GetUtcNow(), cancellationToken))
        {
            throw new OperationCanceledException("The job is no longer owned by this worker.", cancellationToken);
        }
    }

    public static void ValidateProgress(int progressPercent, string stage)
    {
        if (progressPercent is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(progressPercent), "Progress must be between 1 and 99.");
        }

        if (string.IsNullOrWhiteSpace(stage) || !JobProgressStages.IsKnown(stage))
        {
            throw new ArgumentException("A known nonblank stage is required.", nameof(stage));
        }
    }
}

public static class JobProgressStages
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public const string ReadingUpload = "reading-upload";
    public const string DecodingFit = "decoding-fit";
    public const string CleaningActivity = "cleaning-activity";
    public const string SavingActivity = "saving-activity";
    public const string QueueingModelRebuild = "queueing-model-rebuild";

    public const string LoadingEvidence = "loading-evidence";
    public const string BuildingPowerModel = "building-power-model";
    public const string CalibratingPhysics = "calibrating-physics";
    public const string BuildingDescentLimits = "building-descent-limits";
    public const string ValidatingModel = "validating-model";
    public const string SavingModel = "saving-model";

    public const string LoadingPrediction = "loading-prediction";
    public const string ProcessingRoute = "processing-route";
    public const string SimulatingRoute = "simulating-route";
    public const string SavingResult = "saving-result";

    private static readonly HashSet<string> KnownStages = new(StringComparer.Ordinal)
    {
        Queued,
        Running,
        Completed,
        Failed,
        Cancelled,
        ReadingUpload,
        DecodingFit,
        CleaningActivity,
        SavingActivity,
        QueueingModelRebuild,
        LoadingEvidence,
        BuildingPowerModel,
        CalibratingPhysics,
        BuildingDescentLimits,
        ValidatingModel,
        SavingModel,
        LoadingPrediction,
        ProcessingRoute,
        SimulatingRoute,
        SavingResult
    };

    public static bool IsKnown(string stage) => KnownStages.Contains(stage);
}
