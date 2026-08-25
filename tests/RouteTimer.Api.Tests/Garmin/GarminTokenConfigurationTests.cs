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
