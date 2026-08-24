namespace RouteTimer.Services.Validation;

public sealed class ModelBuildException(string code, string message, Exception? innerException = null)
    : RouteTimerJobException(code, message, innerException);
