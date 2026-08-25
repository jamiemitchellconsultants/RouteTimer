using RouteTimer.Api.Auth;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Auth;

public sealed class LocalCredentialServiceTests
{
    [Fact]
    public async Task Setup_is_required_until_a_credential_exists()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository());

        Assert.True(await service.IsSetupRequiredAsync(CancellationToken.None));

        await service.SetupAsync("correct horse battery staple", CancellationToken.None);

        Assert.False(await service.IsSetupRequiredAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Setup_refuses_to_run_a_second_time()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository());
        await service.SetupAsync("correct horse battery staple", CancellationToken.None);

        var result = await service.SetupAsync("a different passphrase", CancellationToken.None);

        Assert.Equal(LocalCredentialSetupResult.AlreadyConfigured, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    public async Task Setup_rejects_a_passphrase_below_the_minimum_length(string passphrase)
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository());

        var result = await service.SetupAsync(passphrase, CancellationToken.None);

        Assert.Equal(LocalCredentialSetupResult.TooShort, result);
        Assert.True(await service.IsSetupRequiredAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Verify_accepts_the_configured_passphrase_and_rejects_others()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository());
        await service.SetupAsync("correct horse battery staple", CancellationToken.None);

        Assert.True(await service.VerifyAsync("correct horse battery staple", CancellationToken.None));
        Assert.False(await service.VerifyAsync("wrong passphrase entirely", CancellationToken.None));
    }

    [Fact]
    public async Task Verify_fails_when_no_credential_has_been_configured()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository());

        Assert.False(await service.VerifyAsync("anything at all", CancellationToken.None));
    }

    [Fact]
    public async Task The_stored_value_is_not_the_passphrase()
    {
        var repository = new InMemoryLocalCredentialRepository();
        var service = new LocalCredentialService(repository);

        await service.SetupAsync("correct horse battery staple", CancellationToken.None);

        Assert.NotNull(repository.Stored);
        Assert.DoesNotContain("correct horse", repository.Stored, StringComparison.Ordinal);
    }

    private sealed class InMemoryLocalCredentialRepository : ILocalCredentialRepository
    {
        public string? Stored { get; private set; }

        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

        public Task SetAsync(string passwordHash, CancellationToken cancellationToken)
        {
            Stored = passwordHash;
            return Task.CompletedTask;
        }
    }
}
