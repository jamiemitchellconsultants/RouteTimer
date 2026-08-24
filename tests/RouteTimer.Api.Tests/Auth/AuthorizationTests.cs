using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RouteTimer.Api.Tests.Auth;

public sealed class AuthorizationTests
{
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
}
