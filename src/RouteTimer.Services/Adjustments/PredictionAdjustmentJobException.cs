using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Adjustments;

public sealed class PredictionAdjustmentJobException(string code, string message, Exception? innerException = null)
    : RouteTimerJobException(code, message, innerException);
