using System.Net;
using System.Security.Claims;
using RouteTimer.Api.Auth;

namespace RouteTimer.Api.Tests.Auth;

public sealed class AuthorizationTests
{
    [Fact]
    public void Keycloak_realm_access_rider_role_is_mapped_to_an_aspnet_role_claim()
    {
        var identity = new ClaimsIdentity("jwt");
        identity.AddClaim(new Claim("realm_access", "{\"roles\":[\"offline_access\",\"rider\"]}"));
        var principal = new ClaimsPrincipal(identity);

        KeycloakRealmRoleMapper.AddRealmRoles(principal);

        Assert.True(principal.IsInRole("rider"));
    }

    [Fact]
    public async Task Profile_endpoint_requires_authentication()
    {
        await using var app = new RouteTimerApiFactory();
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/training/uploads")]
    [InlineData("POST", "/api/predictions")]
    [InlineData("GET", "/api/predictions")]
    [InlineData("GET", "/api/predictions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("GET", "/api/jobs/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    public async Task Api_resources_require_authentication(string method, string path)
    {
        await using var app = new RouteTimerApiFactory();
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (method == "POST")
        {
            request.Content = new MultipartFormDataContent();
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
