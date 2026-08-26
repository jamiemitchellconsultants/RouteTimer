using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RouteTimer.Api.Tests.Garmin;

public sealed class GarminTokenConfigurationTests
{
    [Theory]
    [InlineData(null, "required")]
    [InlineData("not-base64", "base64")]
    [InlineData("AA==", "32 bytes")]
    public void Api_startup_fails_closed_for_invalid_token_encryption_keys(string? encodedKey, string expectedMessage)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // A deployment is what this test is about, and it must not inherit the fixed
            // development key from appsettings.Development.json -- that key exists only so the
            // app starts on a developer machine, and letting it leak in here would quietly turn
            // the null case into "started fine", which is the exact failure this test exists to
            // catch.
            builder.UseEnvironment("Production");
            builder.UseSetting("GarminAdapter:BaseUrl", "http://garmin-adapter.invalid/");
            if (encodedKey is not null)
            {
                builder.UseSetting("Garmin:TokenEncryptionKey", encodedKey);
            }
        });

        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);

        Assert.Contains(expectedMessage, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
