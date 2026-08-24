namespace RouteTimer.Contracts.Errors;

public static class ErrorCodes
{
    public const string ProfileRequired = "profile-required";
    public const string ModelNotReady = "model-not-ready";
    public const string MultipartRequired = "multipart-required";
    public const string PredictionGpxRequired = "prediction-gpx-required";
    public const string InvalidGpxUpload = "invalid-gpx-upload";
    public const string GpxTooLarge = "gpx-too-large";
}
