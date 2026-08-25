namespace RouteTimer.Contracts.Errors;

public static class ErrorCodes
{
    public const string MultipartRequired = "multipart-required";
    public const string InvalidProfile = "invalid-profile";
    public const string TooManyFiles = "too-many-files";
    public const string FitUploadRequired = "fit-upload-required";
    public const string InvalidFitUpload = "invalid-fit-upload";
    public const string FitTooLarge = "fit-too-large";
    public const string ActivityNotFound = "activity-not-found";
    public const string ProfileRequired = "profile-required";
    public const string NoEligibleActivities = "no-eligible-activities";
    public const string ModelNotReady = "model-not-ready";
    public const string InvalidRiderModel = "invalid-rider-model";
    public const string PredictionGpxRequired = "prediction-gpx-required";
    public const string InvalidGpxUpload = "invalid-gpx-upload";
    public const string GpxTooLarge = "gpx-too-large";
    public const string PredictionNotFound = "prediction-not-found";
    public const string JobNotFound = "job-not-found";
    public const string LocalCredentialAlreadyConfigured = "local-credential-already-configured";
    public const string LocalCredentialTooShort = "local-credential-too-short";
    public const string LocalCredentialPadded = "local-credential-padded";
    public const string LocalCredentialTooLong = "local-credential-too-long";
    public const string LocalCredentialRejected = "local-credential-rejected";
    public const string LocalCredentialLockedOut = "local-credential-locked-out";
    public const string CrossSiteRequestRejected = "cross-site-request-rejected";
}
