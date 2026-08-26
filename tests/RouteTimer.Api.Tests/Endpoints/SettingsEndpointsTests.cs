using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Settings;

namespace RouteTimer.Api.Tests.Endpoints;

public sealed class SettingsEndpointsTests
{
    [Fact]
    public async Task Status_never_returns_the_key_in_its_body()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();
        await client.PutAsJsonAsync("/api/settings/google-maps-key", new SaveGoogleMapsKeyRequest("AIzaSyExampleKeyValue0123456789"));

        using var response = await client.GetAsync("/api/settings/google-maps-key");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("AIzaSyExampleKeyValue0123456789", body, StringComparison.Ordinal);
        var status = await response.Content.ReadFromJsonAsync<GoogleMapsKeyStatusResponse>();
        Assert.True(status!.Configured);
        Assert.Equal("AIza…6789", status.Hint);
    }

    [Fact]
    public async Task Put_with_an_empty_key_returns_bad_request()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/settings/google-maps-key", new SaveGoogleMapsKeyRequest(""));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.GoogleMapsKeyInvalid, body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Use_with_nothing_stored_returns_not_found()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();

        using var response = await client.PostAsync("/api/settings/google-maps-key/use", content: null);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.GoogleMapsKeyNotStored, body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_saved_key_round_trips_through_put_then_use()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();
        await client.PutAsJsonAsync("/api/settings/google-maps-key", new SaveGoogleMapsKeyRequest("AIzaSyExampleKeyValue0123456789"));

        using var response = await client.PostAsync("/api/settings/google-maps-key/use", content: null);
        var revealed = await response.Content.ReadFromJsonAsync<GoogleMapsKeyResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AIzaSyExampleKeyValue0123456789", revealed!.ApiKey);
    }

    [Fact]
    public async Task Delete_returns_no_content_twice_in_a_row()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();

        using var first = await client.DeleteAsync("/api/settings/google-maps-key");
        using var second = await client.DeleteAsync("/api/settings/google-maps-key");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task Reports_storage_unavailable_when_no_encryption_key_is_configured()
    {
        await using var app = new RouteTimerApiFactory()
            .WithSetting("GoogleMaps:KeyEncryptionKey", null)
            .WithRiderAuthentication();
        using var client = app.CreateClient();

        using var statusResponse = await client.GetAsync("/api/settings/google-maps-key");
        var status = await statusResponse.Content.ReadFromJsonAsync<GoogleMapsKeyStatusResponse>();
        Assert.False(status!.StorageAvailable);

        using var putResponse = await client.PutAsJsonAsync("/api/settings/google-maps-key", new SaveGoogleMapsKeyRequest("AIzaSyExampleKeyValue0123456789"));
        Assert.Equal(HttpStatusCode.Conflict, putResponse.StatusCode);
    }
}
