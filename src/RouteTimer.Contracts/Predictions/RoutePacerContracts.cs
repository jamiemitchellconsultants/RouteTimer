namespace RouteTimer.Contracts.Predictions;

/// <summary>
/// The origin is reported even while the integration is disabled: the client validates every
/// handoff link against it independently, so it must know the expected origin before it ever
/// receives one.
/// </summary>
public sealed record RoutePacerStatusResponse(bool Enabled, string RoutePacerOrigin);

public sealed record RoutePacerHandoffResponse(string Url, DateTimeOffset ExpiresAt);
