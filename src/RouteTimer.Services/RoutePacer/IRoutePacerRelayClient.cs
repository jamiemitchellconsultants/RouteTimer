namespace RouteTimer.Services.RoutePacer;

public sealed record RoutePacerRelayGrant(Uri PayloadUrl, DateTimeOffset ExpiresAt);

public interface IRoutePacerRelayClient
{
    Task<RoutePacerRelayGrant> UploadAsync(byte[] timedGpx, CancellationToken cancellationToken);
}
