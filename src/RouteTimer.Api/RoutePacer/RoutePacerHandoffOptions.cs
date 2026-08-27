namespace RouteTimer.Api.RoutePacer;

public sealed record RoutePacerHandoffOptions
{
    public const string SectionName = "RoutePacerHandoff";

    public bool Enabled { get; init; }

    public string RoutePacerBaseUrl { get; init; } = "https://pacetracking.tqaentry.com";

    public string RelayUploadKey { get; init; } = string.Empty;

    public string SigningPrivateKeyPem { get; init; } = string.Empty;
}
