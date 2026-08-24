namespace RouteTimer.Services.Validation;

public sealed class ActivityInputException(string code, string message, Exception? innerException = null)
    : RouteTimerJobException(code, message, innerException);
