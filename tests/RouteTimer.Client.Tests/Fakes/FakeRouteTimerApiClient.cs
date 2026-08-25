using RouteTimer.Client.Api;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Models;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Contracts.Profile;
using RouteTimer.Contracts.Training;

namespace RouteTimer.Client.Tests.Fakes;

public sealed class FakeRouteTimerApiClient : IRouteTimerApiClient
{
    public Func<CancellationToken, Task<ProfileResponse?>>? OnGetProfileAsync { get; set; }
    public Func<UpdateProfileRequest, CancellationToken, Task<ProfileResponse>>? OnUpdateProfileAsync { get; set; }
    public Func<CancellationToken, Task<IReadOnlyList<TrainingActivitySummaryResponse>>>? OnGetTrainingActivitiesAsync { get; set; }
    public Func<Guid, CancellationToken, Task<TrainingActivityDetailResponse?>>? OnGetTrainingActivityAsync { get; set; }
    public Func<IReadOnlyList<ClientFileUpload>, CancellationToken, Task<TrainingUploadBatchResponse>>? OnUploadTrainingActivitiesAsync { get; set; }
    public Func<Guid, CancellationToken, Task<bool>>? OnDeleteTrainingActivityAsync { get; set; }
    public Func<CancellationToken, Task<ModelStatusResponse>>? OnGetModelStatusAsync { get; set; }
    public Func<CancellationToken, Task<IReadOnlyList<PredictionSummaryResponse>>>? OnGetPredictionsAsync { get; set; }

    public Queue<JobResponse?> Jobs { get; } = new();

    public List<(Guid JobId, CancellationToken CancellationToken)> RequestedJobs { get; } = [];
    public List<CancellationToken> RequestedProfiles { get; } = [];
    public List<(UpdateProfileRequest Request, CancellationToken CancellationToken)> UpdatedProfiles { get; } = [];
    public List<CancellationToken> RequestedTrainingActivities { get; } = [];
    public List<(Guid ActivityId, CancellationToken CancellationToken)> RequestedTrainingActivityDetails { get; } = [];
    public List<(IReadOnlyList<ClientFileUpload> Files, CancellationToken CancellationToken)> UploadedTrainingActivities { get; } = [];
    public List<(Guid ActivityId, CancellationToken CancellationToken)> DeletedTrainingActivities { get; } = [];
    public List<CancellationToken> RequestedModelStatuses { get; } = [];
    public List<CancellationToken> RequestedPredictions { get; } = [];

    public Task<ProfileResponse?> GetProfileAsync(CancellationToken ct)
    {
        RequestedProfiles.Add(ct);
        return OnGetProfileAsync is not null
            ? OnGetProfileAsync(ct)
            : throw new NotSupportedException();
    }

    public Task<ProfileResponse> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct)
    {
        UpdatedProfiles.Add((request, ct));
        return OnUpdateProfileAsync is not null
            ? OnUpdateProfileAsync(request, ct)
            : throw new NotSupportedException();
    }

    public Task<IReadOnlyList<TrainingActivitySummaryResponse>> GetTrainingActivitiesAsync(CancellationToken ct)
    {
        RequestedTrainingActivities.Add(ct);
        return OnGetTrainingActivitiesAsync is not null
            ? OnGetTrainingActivitiesAsync(ct)
            : throw new NotSupportedException();
    }

    public Task<TrainingActivityDetailResponse?> GetTrainingActivityAsync(Guid id, CancellationToken ct)
    {
        RequestedTrainingActivityDetails.Add((id, ct));
        return OnGetTrainingActivityAsync is not null
            ? OnGetTrainingActivityAsync(id, ct)
            : throw new NotSupportedException();
    }

    public Task<TrainingUploadBatchResponse> UploadTrainingActivitiesAsync(IReadOnlyList<ClientFileUpload> files, CancellationToken ct)
    {
        UploadedTrainingActivities.Add((files.ToArray(), ct));
        return OnUploadTrainingActivitiesAsync is not null
            ? OnUploadTrainingActivitiesAsync(files, ct)
            : throw new NotSupportedException();
    }

    public Task<bool> DeleteTrainingActivityAsync(Guid id, CancellationToken ct)
    {
        DeletedTrainingActivities.Add((id, ct));
        return OnDeleteTrainingActivityAsync is not null
            ? OnDeleteTrainingActivityAsync(id, ct)
            : throw new NotSupportedException();
    }

    public Task<ModelStatusResponse> GetModelStatusAsync(CancellationToken ct)
    {
        RequestedModelStatuses.Add(ct);
        return OnGetModelStatusAsync is not null
            ? OnGetModelStatusAsync(ct)
            : throw new NotSupportedException();
    }

    public Task<IReadOnlyList<PredictionSummaryResponse>> GetPredictionsAsync(CancellationToken ct)
    {
        RequestedPredictions.Add(ct);
        return OnGetPredictionsAsync is not null
            ? OnGetPredictionsAsync(ct)
            : throw new NotSupportedException();
    }

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
