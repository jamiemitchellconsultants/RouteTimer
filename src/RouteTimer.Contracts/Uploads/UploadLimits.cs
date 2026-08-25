namespace RouteTimer.Contracts.Uploads;

public static class UploadLimits
{
    public const long MaximumFileBytes = 50L * 1024 * 1024;
    public const int MaximumTrainingFiles = 10;
    public const long MaximumTrainingRequestBytes = MaximumTrainingFiles * MaximumFileBytes + 1024 * 1024;
}
