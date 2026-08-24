namespace RouteTimer.Services.Predictions;

public sealed class PredictionSubmissionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
