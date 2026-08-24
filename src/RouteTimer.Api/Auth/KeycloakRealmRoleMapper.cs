using System.Security.Claims;
using System.Text.Json;

namespace RouteTimer.Api.Auth;

/// <summary>Maps Keycloak's nested realm-access roles into ASP.NET Core role claims.</summary>
public static class KeycloakRealmRoleMapper
{
    public static void AddRealmRoles(ClaimsPrincipal? principal)
    {
        var identity = principal?.Identity as ClaimsIdentity;
        var realmAccess = principal?.FindFirst("realm_access")?.Value;
        if (identity is null || string.IsNullOrWhiteSpace(realmAccess))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(realmAccess);
            if (!document.RootElement.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var role in roles.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role!));
            }
        }
        catch (JsonException)
        {
            // Invalid optional role metadata must not invalidate an otherwise valid access token.
        }
    }
}
