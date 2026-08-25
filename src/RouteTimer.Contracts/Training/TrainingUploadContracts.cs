namespace RouteTimer.Contracts.Training;

public sealed record TrainingUploadBatchResponse(IReadOnlyList<TrainingUploadFileResponse> Files);

public sealed record TrainingUploadFileResponse(
    string FileName,
    string Outcome,
    Guid? UploadId,
    Guid? JobId,
    string? ErrorCode);
