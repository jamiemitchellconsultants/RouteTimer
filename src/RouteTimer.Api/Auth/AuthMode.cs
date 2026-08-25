namespace RouteTimer.Api.Auth;

/// <summary>How a request is authenticated. Selected per deployment; there is no default.</summary>
public enum AuthMode
{
    /// <summary>Single-rider passphrase held by this deployment, used for local installations.</summary>
    Local,

    /// <summary>Bearer tokens issued by an external Keycloak realm.</summary>
    Keycloak
}

public static class AuthModeResolver
{
    public const string ConfigurationKey = "Auth:Mode";

    /// <summary>
    /// Reads the deployment's authentication mode. There is deliberately no default: a deployment
    /// that does not state what it is must not start, because guessing wrong in either direction is
    /// worse than refusing to run.
    /// </summary>
    public static AuthMode Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[ConfigurationKey];
        if (Enum.TryParse<AuthMode>(configured, ignoreCase: true, out var mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        throw new InvalidOperationException(
            $"{ConfigurationKey} must be set to either 'Local' or 'Keycloak'. " +
            $"The configured value was {(string.IsNullOrWhiteSpace(configured) ? "not set" : $"'{configured}'")}. " +
            "Local mode authenticates with a passphrase set on first use; Keycloak mode authenticates " +
            "bearer tokens from the authority in Keycloak:Authority.");
    }
}
