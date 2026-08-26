namespace RouteTimer.Contracts.Settings;

public sealed record GoogleMapsKeyStatusResponse(bool Configured, string? Hint, bool StorageAvailable);

public sealed record SaveGoogleMapsKeyRequest(string ApiKey)
{
    public override string ToString() => "SaveGoogleMapsKeyRequest { ApiKey = <redacted> }";
}

public sealed record GoogleMapsKeyResponse(string ApiKey)
{
    public override string ToString() => "GoogleMapsKeyResponse { ApiKey = <redacted> }";
}
