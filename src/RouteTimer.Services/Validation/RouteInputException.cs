namespace RouteTimer.Services.Validation;

public sealed class RouteInputException(string message) : Exception(message);
