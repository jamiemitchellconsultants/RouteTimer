namespace RouteTimer.Services.Persistence;

public sealed class InvalidPersistedRiderModelException : InvalidOperationException
{
    public InvalidPersistedRiderModelException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
