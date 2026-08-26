using System.Net;
using Microsoft.Extensions.Configuration;
using RouteTimer.Api.Auth;

namespace RouteTimer.Api.Tests.Auth;

public sealed class AuthModeTests
{
    [Theory]
    [InlineData("Local", AuthMode.Local)]
    [InlineData("local", AuthMode.Local)]
    [InlineData("Keycloak", AuthMode.Keycloak)]
    [InlineData("KEYCLOAK", AuthMode.Keycloak)]
    public void Resolve_accepts_either_mode_case_insensitively(string configured, AuthMode expected)
    {
        var configuration = Build(configured);

        Assert.Equal(expected, AuthModeResolver.Resolve(configuration));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("None")]
    [InlineData("Anonymous")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("Local,Keycloak")]
    [InlineData("Local, Keycloak")]
    public void Resolve_fails_fast_when_the_mode_is_missing_or_unrecognised(string? configured)
    {
        var configuration = Build(configured);

        var exception = Assert.Throws<InvalidOperationException>(() => AuthModeResolver.Resolve(configuration));

        Assert.Contains("Auth:Mode", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Local", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Keycloak", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_application_refuses_to_start_without_an_authentication_mode()
    {
        using var app = new RouteTimerApiFactory().WithAuthMode(string.Empty);

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => app.CreateClient());

        Assert.Contains("Auth:Mode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Keycloak_mode_refuses_to_start_without_an_authority()
    {
        using var app = new RouteTimerApiFactory()
            .WithAuthMode("Keycloak")
            .WithSetting("Keycloak:Authority", null);

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => app.CreateClient());

        Assert.Contains("Keycloak:Authority", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_application_starts_in_local_mode()
    {
        await using var app = new RouteTimerApiFactory().WithAuthMode("Local");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static IConfiguration Build(string? configured) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Mode"] = configured })
            .Build();
}
