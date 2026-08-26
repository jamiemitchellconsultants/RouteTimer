using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using RouteTimer.Contracts.Auth;

namespace RouteTimer.Client.Auth;

/// <summary>
/// Reports local-mode authentication state by asking the API whether the session cookie the browser
/// is already sending is valid. The client never sees the cookie itself.
/// </summary>
public sealed class LocalAuthenticationStateProvider(HttpClient http) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var session = await http.GetFromJsonAsync<AuthSessionResponse>("api/auth/session");
            if (session?.Authenticated != true)
            {
                return Anonymous;
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "rider"),
                    new Claim(ClaimTypes.Role, "rider")
                ],
                authenticationType: "RouteTimerLocal",
                nameType: ClaimTypes.Name,
                roleType: ClaimTypes.Role);

            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // A failed session check is treated as "not signed in" rather than propagated: this
            // return value feeds CascadingAuthenticationState directly, and an uncaught exception
            // here breaks authentication-state rendering for the whole component tree. Malformed
            // JSON and a timed-out request are just as much "could not confirm a session" as a
            // network error is.
            return Anonymous;
        }
    }

    /// <summary>Call after sign-in, first-run setup, or sign-out so the UI re-reads the session.</summary>
    public void NotifySessionChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}
