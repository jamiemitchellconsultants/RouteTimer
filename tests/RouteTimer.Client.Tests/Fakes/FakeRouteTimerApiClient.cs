using RouteTimer.Client.Api;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Models;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Contracts.Profile;
using RouteTimer.Contracts.Training;

namespace RouteTimer.Client.Tests.Fakes;

public sealed class FakeRouteTimerApiClient : IRouteTimerApiClient
{
    public Queue<JobResponse?> Jobs { get; } = new();

    public List<(Guid JobId, CancellationToken CancellationToken)> RequestedJobs { get; } = [];

    public Task<ProfileResponse?> GetProfileAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<ProfileResponse> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<TrainingActivitySummaryResponse>> GetTrainingActivitiesAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<TrainingActivityDetailResponse?> GetTrainingActivityAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<TrainingUploadBatchResponse> UploadTrainingActivitiesAsync(IReadOnlyList<ClientFileUpload> files, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> DeleteTrainingActivityAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<ModelStatusResponse> GetModelStatusAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<ModelRebuildResponse> RebuildModelAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<PredictionSummaryResponse>> GetPredictionsAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<PredictionSubmissionResponse> SubmitPredictionAsync(ClientFileUpload file, CancellationToken ct) => throw new NotSupportedException();
    public Task<PredictionDetailResponse?> GetPredictionAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> DeletePredictionAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task<JobResponse?> GetJobAsync(Guid id, CancellationToken ct)
    {
        RequestedJobs.Add((id, ct));

        if (Jobs.Count == 0)
        {
            throw new InvalidOperationException("No queued job response was configured.");
        }

        return Task.FromResult(Jobs.Dequeue());
    }
}
