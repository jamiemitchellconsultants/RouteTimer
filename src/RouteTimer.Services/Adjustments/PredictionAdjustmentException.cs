namespace RouteTimer.Services.Adjustments;

public sealed class PredictionAdjustmentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
