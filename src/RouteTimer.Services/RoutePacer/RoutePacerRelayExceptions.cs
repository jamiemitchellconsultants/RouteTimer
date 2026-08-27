namespace RouteTimer.Services.RoutePacer;

public enum RoutePacerRelayFailure
{
    Authentication,
    PayloadTooLarge,
    RejectedPayload,
    RateLimited,
    Unavailable,
    InvalidResponse
}

// Messages are fixed strings chosen at the throw site. Nothing derived from the relay -- a
// response body, the request URL, the route name, the credential -- may reach one, because these
// travel into logs and, through the endpoint mapping, into problem details the browser can read.
public sealed class RoutePacerRelayException(
    RoutePacerRelayFailure failure,
    string message,
    TimeSpan? retryAfter = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public RoutePacerRelayFailure Failure { get; } = failure;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
