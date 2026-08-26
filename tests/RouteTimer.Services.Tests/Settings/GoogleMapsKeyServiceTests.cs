using Microsoft.Extensions.Time.Testing;
using RouteTimer.Services.Security;
using RouteTimer.Services.Settings;

namespace RouteTimer.Services.Tests.Settings;

public sealed class GoogleMapsKeyServiceTests
{
    private readonly FakeGoogleMapsCredentialRepository repository = new();
    private readonly FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Reports_storage_unavailable_when_no_protector_is_configured()
    {
        var service = new GoogleMapsKeyService(repository, protector: null, time);

        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.False(status.StorageAvailable);
        Assert.False(status.Configured);
        Assert.Null(status.Hint);
        await Assert.ThrowsAsync<GoogleMapsKeyStorageUnavailableException>(
            () => service.SaveAsync("AIzaSyExampleKeyValue0123456789", CancellationToken.None));
    }

    [Fact]
    public async Task Saves_a_key_and_reports_a_masked_hint_without_revealing_it()
    {
        var service = CreateService();

        await service.SaveAsync("AIzaSyExampleKeyValue0123456789", CancellationToken.None);
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.True(status.Configured);
        Assert.Equal("AIza…6789", status.Hint);
    }

    [Fact]
    public async Task Reveals_the_saved_key()
    {
        var service = CreateService();
        await service.SaveAsync("AIzaSyExampleKeyValue0123456789", CancellationToken.None);

        Assert.Equal("AIzaSyExampleKeyValue0123456789", await service.RevealAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Fails_to_reveal_when_nothing_is_stored()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<GoogleMapsKeyNotStoredException>(
            () => service.RevealAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_an_empty_key(string key)
    {
        var service = CreateService();

        await Assert.ThrowsAsync<GoogleMapsKeyInvalidException>(
            () => service.SaveAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_a_key_longer_than_the_permitted_length()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<GoogleMapsKeyInvalidException>(
            () => service.SaveAsync(new string('a', 513), CancellationToken.None));
    }

    [Fact]
    public async Task Deletes_the_stored_key()
    {
        var service = CreateService();
        await service.SaveAsync("AIzaSyExampleKeyValue0123456789", CancellationToken.None);

        await service.DeleteAsync(CancellationToken.None);

        Assert.False((await service.GetStatusAsync(CancellationToken.None)).Configured);
    }

    private GoogleMapsKeyService CreateService() => new(
        repository,
        new AesGcmSecretProtector(new byte[32], "RouteTimer:GoogleMapsKey:1:1"),
        time);
}
