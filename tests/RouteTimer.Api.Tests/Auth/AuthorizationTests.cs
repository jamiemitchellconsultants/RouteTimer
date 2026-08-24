using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
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
        await using var app = new WebApplicationFactory<Program>();
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Training_upload_endpoint_requires_authentication()
    {
        await using var app = new WebApplicationFactory<Program>();
        using var client = app.CreateClient();

        using var response = await client.PostAsync("/api/training/uploads", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Prediction_submission_endpoint_requires_authentication()
    {
        await using var app = new WebApplicationFactory<Program>();
        using var client = app.CreateClient();

        using var response = await client.PostAsync("/api/predictions", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
