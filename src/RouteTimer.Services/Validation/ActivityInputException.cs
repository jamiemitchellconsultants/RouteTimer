namespace RouteTimer.Services.Validation;

public sealed class ActivityInputException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
