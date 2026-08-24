namespace RouteTimer.Contracts.Training;

public sealed record TrainingUploadResponse(string FileName, string Outcome, string? ErrorCode);
