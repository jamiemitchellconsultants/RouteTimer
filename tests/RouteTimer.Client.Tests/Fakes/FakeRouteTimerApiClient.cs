using RouteTimer.Client.Api;
using RouteTimer.Contracts.Garmin;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Models;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Contracts.Profile;
using RouteTimer.Contracts.Routes;
using RouteTimer.Contracts.Settings;
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
    public Func<CancellationToken, Task<ModelRebuildResponse>>? OnRebuildModelAsync { get; set; }
    public Func<CancellationToken, Task<IReadOnlyList<PredictionSummaryResponse>>>? OnGetPredictionsAsync { get; set; }
    public Func<ClientFileUpload, CancellationToken, Task<PredictionSubmissionResponse>>? OnSubmitPredictionAsync { get; set; }
    public Func<Guid, CancellationToken, Task<PredictionDetailResponse?>>? OnGetPredictionAsync { get; set; }
    public Func<Guid, CancellationToken, Task<bool>>? OnDeletePredictionAsync { get; set; }
    public Func<Guid, CancellationToken, Task<JobResponse?>>? OnGetJobAsync { get; set; }
    public Func<CancellationToken, Task<GarminConnectionResponse>>? OnGetGarminConnectionAsync { get; set; }
    public Func<GarminLoginRequest, CancellationToken, Task<GarminConnectionResponse>>? OnLoginGarminAsync { get; set; }
    public Func<GarminMfaRequest, CancellationToken, Task<GarminConnectionResponse>>? OnCompleteGarminMfaAsync { get; set; }
    public Func<CancellationToken, Task>? OnDisconnectGarminAsync { get; set; }
    public Func<string?, CancellationToken, Task<GarminActivityPageResponse>>? OnGetGarminActivitiesAsync { get; set; }
    public Func<GarminImportRequest, CancellationToken, Task<GarminImportBatchResponse>>? OnImportGarminActivitiesAsync { get; set; }

    public Func<CancellationToken, Task>? OnLocalLogoutAsync { get; set; }
    public Func<string, CancellationToken, Task<bool>>? OnSetupLocalCredentialAsync { get; set; }
    public Func<string, CancellationToken, Task<bool>>? OnLocalLoginAsync { get; set; }
    public Func<string, CancellationToken, Task<ShortLinkResponse>>? OnResolveShortLinkAsync { get; set; }
    public Func<Guid, CreateGarminCourseRequest, CancellationToken, Task<GarminCourseResponse>>? OnCreateGarminCourseAsync { get; set; }
    public Func<CancellationToken, Task<RoutePacerStatusResponse>>? OnGetRoutePacerStatusAsync { get; set; }
    public Func<Guid, CancellationToken, Task<RoutePacerHandoffResponse>>? OnCreateRoutePacerHandoffAsync { get; set; }
    public List<CancellationToken> RequestedRoutePacerStatuses { get; } = [];
    public List<(Guid PredictionId, CancellationToken CancellationToken)> CreatedRoutePacerHandoffs { get; } = [];
    public Func<CancellationToken, Task<GoogleMapsKeyStatusResponse>>? OnGetGoogleMapsKeyStatusAsync { get; set; }
    public Func<SaveGoogleMapsKeyRequest, CancellationToken, Task>? OnSaveGoogleMapsKeyAsync { get; set; }
    public Func<CancellationToken, Task>? OnDeleteGoogleMapsKeyAsync { get; set; }
    public Func<CancellationToken, Task<GoogleMapsKeyResponse>>? OnUseGoogleMapsKeyAsync { get; set; }

    public Queue<JobResponse?> Jobs { get; } = new();

    public List<(Guid JobId, CancellationToken CancellationToken)> RequestedJobs { get; } = [];
    public List<CancellationToken> LocalLogouts { get; } = [];
    public List<(string Passphrase, CancellationToken CancellationToken)> SetupLocalCredentials { get; } = [];
    public List<(string Passphrase, CancellationToken CancellationToken)> LocalLogins { get; } = [];
    public List<CancellationToken> RequestedProfiles { get; } = [];
    public List<(UpdateProfileRequest Request, CancellationToken CancellationToken)> UpdatedProfiles { get; } = [];
    public List<CancellationToken> RequestedTrainingActivities { get; } = [];
    public List<(Guid ActivityId, CancellationToken CancellationToken)> RequestedTrainingActivityDetails { get; } = [];
    public List<(IReadOnlyList<ClientFileUpload> Files, CancellationToken CancellationToken)> UploadedTrainingActivities { get; } = [];
    public List<(Guid ActivityId, CancellationToken CancellationToken)> DeletedTrainingActivities { get; } = [];
    public List<CancellationToken> RequestedModelStatuses { get; } = [];
    public List<CancellationToken> RequestedModelRebuilds { get; } = [];
    public List<CancellationToken> RequestedPredictions { get; } = [];
    public List<(ClientFileUpload File, CancellationToken CancellationToken)> SubmittedPredictions { get; } = [];
    public List<(Guid PredictionId, CancellationToken CancellationToken)> RequestedPredictionDetails { get; } = [];
    public List<(Guid PredictionId, CancellationToken CancellationToken)> DeletedPredictions { get; } = [];
    public List<CancellationToken> RequestedGarminConnections { get; } = [];
    public List<(GarminLoginRequest Request, CancellationToken CancellationToken)> GarminLoginRequests { get; } = [];
    public List<(GarminMfaRequest Request, CancellationToken CancellationToken)> GarminMfaRequests { get; } = [];
    public List<CancellationToken> DisconnectedGarminConnections { get; } = [];
    public List<(string? Cursor, CancellationToken CancellationToken)> RequestedGarminActivities { get; } = [];
    public List<(GarminImportRequest Request, CancellationToken CancellationToken)> GarminImportRequests { get; } = [];

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

    public Task<ModelRebuildResponse> RebuildModelAsync(CancellationToken ct)
    {
        RequestedModelRebuilds.Add(ct);
        return OnRebuildModelAsync is not null
            ? OnRebuildModelAsync(ct)
            : throw new NotSupportedException();
    }

    public Task<IReadOnlyList<PredictionSummaryResponse>> GetPredictionsAsync(CancellationToken ct)
    {
        RequestedPredictions.Add(ct);
        return OnGetPredictionsAsync is not null
            ? OnGetPredictionsAsync(ct)
            : throw new NotSupportedException();
    }

    public Task<PredictionSubmissionResponse> SubmitPredictionAsync(ClientFileUpload file, CancellationToken ct)
    {
        SubmittedPredictions.Add((file, ct));
        return OnSubmitPredictionAsync is not null
            ? OnSubmitPredictionAsync(file, ct)
            : throw new NotSupportedException();
    }

    public Task<PredictionDetailResponse?> GetPredictionAsync(Guid id, CancellationToken ct)
    {
        RequestedPredictionDetails.Add((id, ct));
        return OnGetPredictionAsync is not null
            ? OnGetPredictionAsync(id, ct)
            : throw new NotSupportedException();
    }

    public Task<bool> DeletePredictionAsync(Guid id, CancellationToken ct)
    {
        DeletedPredictions.Add((id, ct));
        return OnDeletePredictionAsync is not null
            ? OnDeletePredictionAsync(id, ct)
            : throw new NotSupportedException();
    }

    public Task<JobResponse?> GetJobAsync(Guid id, CancellationToken ct)
    {
        RequestedJobs.Add((id, ct));

        if (OnGetJobAsync is not null)
        {
            return OnGetJobAsync(id, ct);
        }

        if (Jobs.Count == 0)
        {
            throw new InvalidOperationException("No queued job response was configured.");
        }

        return Task.FromResult(Jobs.Dequeue());
    }

    public Task<GarminConnectionResponse> GetGarminConnectionAsync(CancellationToken ct)
    {
        RequestedGarminConnections.Add(ct);
        return OnGetGarminConnectionAsync is not null
            ? OnGetGarminConnectionAsync(ct)
            : Task.FromResult(NotConnected());
    }

    public Task<GarminConnectionResponse> LoginGarminAsync(GarminLoginRequest request, CancellationToken ct)
    {
        GarminLoginRequests.Add((request, ct));
        return OnLoginGarminAsync is not null
            ? OnLoginGarminAsync(request, ct)
            : Task.FromResult(NotConnected());
    }

    public Task<GarminConnectionResponse> CompleteGarminMfaAsync(GarminMfaRequest request, CancellationToken ct)
    {
        GarminMfaRequests.Add((request, ct));
        return OnCompleteGarminMfaAsync is not null
            ? OnCompleteGarminMfaAsync(request, ct)
            : Task.FromResult(NotConnected());
    }

    public Task DisconnectGarminAsync(CancellationToken ct)
    {
        DisconnectedGarminConnections.Add(ct);
        return OnDisconnectGarminAsync is not null
            ? OnDisconnectGarminAsync(ct)
            : Task.CompletedTask;
    }

    public Task<GarminActivityPageResponse> GetGarminActivitiesAsync(string? cursor, CancellationToken ct)
    {
        RequestedGarminActivities.Add((cursor, ct));
        return OnGetGarminActivitiesAsync is not null
            ? OnGetGarminActivitiesAsync(cursor, ct)
            : Task.FromResult(new GarminActivityPageResponse([], null));
    }

    public Task<GarminImportBatchResponse> ImportGarminActivitiesAsync(GarminImportRequest request, CancellationToken ct)
    {
        GarminImportRequests.Add((request, ct));
        return OnImportGarminActivitiesAsync is not null
            ? OnImportGarminActivitiesAsync(request, ct)
            : Task.FromResult(new GarminImportBatchResponse([]));
    }

    public Task LocalLogoutAsync(CancellationToken ct)
    {
        LocalLogouts.Add(ct);
        return OnLocalLogoutAsync is not null
            ? OnLocalLogoutAsync(ct)
            : Task.CompletedTask;
    }

    public Task<bool> SetupLocalCredentialAsync(string passphrase, CancellationToken ct)
    {
        SetupLocalCredentials.Add((passphrase, ct));
        return OnSetupLocalCredentialAsync is not null
            ? OnSetupLocalCredentialAsync(passphrase, ct)
            : throw new NotSupportedException();
    }

    public Task<bool> LocalLoginAsync(string passphrase, CancellationToken ct)
    {
        LocalLogins.Add((passphrase, ct));
        return OnLocalLoginAsync is not null
            ? OnLocalLoginAsync(passphrase, ct)
            : throw new NotSupportedException();
    }

    public Task<ShortLinkResponse> ResolveShortLinkAsync(string code, CancellationToken ct) =>
        OnResolveShortLinkAsync is not null
            ? OnResolveShortLinkAsync(code, ct)
            : throw new NotSupportedException();

    public Task<GarminCourseResponse> CreateGarminCourseAsync(Guid predictionId, CreateGarminCourseRequest request, CancellationToken ct) =>
        OnCreateGarminCourseAsync is not null
            ? OnCreateGarminCourseAsync(predictionId, request, ct)
            : throw new NotSupportedException();

    public Task<GoogleMapsKeyStatusResponse> GetGoogleMapsKeyStatusAsync(CancellationToken ct) =>
        OnGetGoogleMapsKeyStatusAsync is not null
            ? OnGetGoogleMapsKeyStatusAsync(ct)
            : throw new NotSupportedException();

    public Task SaveGoogleMapsKeyAsync(SaveGoogleMapsKeyRequest request, CancellationToken ct) =>
        OnSaveGoogleMapsKeyAsync is not null
            ? OnSaveGoogleMapsKeyAsync(request, ct)
            : throw new NotSupportedException();

    public Task DeleteGoogleMapsKeyAsync(CancellationToken ct) =>
        OnDeleteGoogleMapsKeyAsync is not null
            ? OnDeleteGoogleMapsKeyAsync(ct)
            : Task.CompletedTask;

    public Task<GoogleMapsKeyResponse> UseGoogleMapsKeyAsync(CancellationToken ct) =>
        OnUseGoogleMapsKeyAsync is not null
            ? OnUseGoogleMapsKeyAsync(ct)
            : throw new NotSupportedException();

    public Task<RoutePacerStatusResponse> GetRoutePacerStatusAsync(CancellationToken ct)
    {
        RequestedRoutePacerStatuses.Add(ct);
        // Disabled unless a test says otherwise: every existing prediction-page test asserts the
        // page as it looks without the integration, and a default-on fake would change all of them.
        return OnGetRoutePacerStatusAsync is not null
            ? OnGetRoutePacerStatusAsync(ct)
            : Task.FromResult(new RoutePacerStatusResponse(false, "https://pacetracking.tqaentry.com"));
    }

    public Task<RoutePacerHandoffResponse> CreateRoutePacerHandoffAsync(Guid predictionId, CancellationToken ct)
    {
        CreatedRoutePacerHandoffs.Add((predictionId, ct));
        return OnCreateRoutePacerHandoffAsync is not null
            ? OnCreateRoutePacerHandoffAsync(predictionId, ct)
            : throw new NotSupportedException();
    }

    private static GarminConnectionResponse NotConnected() =>
        new("not-connected", null, null, null);
}
