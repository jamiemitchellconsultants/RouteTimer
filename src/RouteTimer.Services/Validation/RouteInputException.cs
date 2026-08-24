namespace RouteTimer.Services.Validation;

public sealed class RouteInputException : Exception
{
    public RouteInputException(string message) : base(message) { }

    public RouteInputException(string message, Exception innerException) : base(message, innerException) { }
}
