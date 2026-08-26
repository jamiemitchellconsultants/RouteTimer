namespace RouteTimer.Contracts.Auth;

/// <summary>
/// The authentication configuration for this deployment, read by the client at startup. This
/// replaces build-time configuration so that one published image serves every deployment.
/// </summary>
/// <param name="Mode">Either "Local" or "Keycloak".</param>
/// <param name="SetupRequired">Local mode only: no passphrase has been set yet.</param>
/// <param name="Authority">Keycloak mode only: the realm's issuer URL.</param>
/// <param name="ClientId">Keycloak mode only: the public SPA client id.</param>
/// <param name="RedirectUri">Keycloak mode only: the login callback path.</param>
/// <param name="PostLogoutRedirectUri">Keycloak mode only: where to land after sign-out.</param>
public sealed record AuthConfigResponse(
    string Mode,
    bool SetupRequired,
    string? Authority,
    string? ClientId,
    string? RedirectUri,
    string? PostLogoutRedirectUri)
{
    /// <summary>The <see cref="Mode"/> value for a local, passphrase-authenticated deployment.</summary>
    public const string LocalMode = "Local";

    /// <summary>The <see cref="Mode"/> value for a deployment authenticated against Keycloak.</summary>
    public const string KeycloakMode = "Keycloak";
}

/// <param name="Authenticated">Whether the caller currently holds a valid session.</param>
public sealed record AuthSessionResponse(bool Authenticated);

/// <param name="Passphrase">The passphrase to set on first use.</param>
public sealed record SetLocalCredentialRequest(string Passphrase);

/// <param name="Passphrase">The passphrase to sign in with.</param>
public sealed record LocalLoginRequest(string Passphrase);
