using RouteTimer.Contracts.Auth;

namespace RouteTimer.Client.Auth;

/// <summary>
/// The deployment's authentication configuration, fetched once before the host is built. Held as a
/// singleton so pages can ask which mode they are running in without another round trip.
/// </summary>
public sealed class ClientAuthConfig(AuthConfigResponse response)
{
    public bool IsLocal => string.Equals(response.Mode, AuthConfigResponse.LocalMode, StringComparison.OrdinalIgnoreCase);

    public bool SetupRequired => response.SetupRequired;

    public AuthConfigResponse Response => response;
}
