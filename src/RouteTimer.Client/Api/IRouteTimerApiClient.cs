using RouteTimer.Contracts.Garmin;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Models;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Contracts.Profile;
using RouteTimer.Contracts.Routes;
using RouteTimer.Contracts.Training;

namespace RouteTimer.Client.Api;

public interface IRouteTimerApiClient
{
    Task<ProfileResponse?> GetProfileAsync(CancellationToken ct);
    Task<ProfileResponse> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct);
    Task<IReadOnlyList<TrainingActivitySummaryResponse>> GetTrainingActivitiesAsync(CancellationToken ct);
    Task<TrainingActivityDetailResponse?> GetTrainingActivityAsync(Guid id, CancellationToken ct);
    Task<TrainingUploadBatchResponse> UploadTrainingActivitiesAsync(IReadOnlyList<ClientFileUpload> files, CancellationToken ct);
    Task<bool> DeleteTrainingActivityAsync(Guid id, CancellationToken ct);
    Task<ModelStatusResponse> GetModelStatusAsync(CancellationToken ct);
    Task<ModelRebuildResponse> RebuildModelAsync(CancellationToken ct);
    Task<IReadOnlyList<PredictionSummaryResponse>> GetPredictionsAsync(CancellationToken ct);
    Task<PredictionSubmissionResponse> SubmitPredictionAsync(ClientFileUpload file, CancellationToken ct);
    Task<PredictionDetailResponse?> GetPredictionAsync(Guid id, CancellationToken ct);
    Task<bool> DeletePredictionAsync(Guid id, CancellationToken ct);
    Task<JobResponse?> GetJobAsync(Guid id, CancellationToken ct);
    Task<GarminConnectionResponse> GetGarminConnectionAsync(CancellationToken ct);
    Task<GarminConnectionResponse> LoginGarminAsync(GarminLoginRequest request, CancellationToken ct);
    Task<GarminConnectionResponse> CompleteGarminMfaAsync(GarminMfaRequest request, CancellationToken ct);
    Task DisconnectGarminAsync(CancellationToken ct);
    Task<GarminActivityPageResponse> GetGarminActivitiesAsync(string? cursor, CancellationToken ct);
    Task<GarminImportBatchResponse> ImportGarminActivitiesAsync(GarminImportRequest request, CancellationToken ct);
    Task LocalLogoutAsync(CancellationToken ct);
    Task<bool> SetupLocalCredentialAsync(string passphrase, CancellationToken ct);
    Task<bool> LocalLoginAsync(string passphrase, CancellationToken ct);
    Task<ShortLinkResponse> ResolveShortLinkAsync(string code, CancellationToken ct);
}
