using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Predictions;

public sealed class PredictionJobException(string code, string message, Exception? innerException = null)
    : RouteTimerJobException(code, message, innerException);
