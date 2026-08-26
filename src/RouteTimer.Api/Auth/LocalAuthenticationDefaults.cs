namespace RouteTimer.Api.Auth;

public static class LocalAuthenticationDefaults
{
    public const string AuthenticationScheme = "RouteTimerLocal";

    public const string CookieName = "routetimer.session";

    /// <summary>
    /// The rider role the authorization policy requires. Local mode grants it to the single rider
    /// this deployment serves; Keycloak mode receives the same role from the realm.
    /// </summary>
    public const string RiderRole = "rider";
}
