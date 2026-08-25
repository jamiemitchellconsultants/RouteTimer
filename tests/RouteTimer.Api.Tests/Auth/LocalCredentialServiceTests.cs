using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RouteTimer.Api.Auth;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Auth;

public sealed class LocalCredentialServiceTests
{
    [Fact]
    public async Task Setup_is_required_until_a_credential_exists()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository(), NullLogger<LocalCredentialService>.Instance);

        Assert.True(await service.IsSetupRequiredAsync(CancellationToken.None));

        await service.SetupAsync("correct horse battery staple", CancellationToken.None);

        Assert.False(await service.IsSetupRequiredAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Setup_refuses_to_run_a_second_time_and_does_not_change_the_stored_hash()
    {
        var repository = new InMemoryLocalCredentialRepository();
        var service = new LocalCredentialService(repository, NullLogger<LocalCredentialService>.Instance);
        await service.SetupAsync("correct horse battery staple", CancellationToken.None);
        var storedAfterFirstSetup = repository.Stored;

        var result = await service.SetupAsync("a different passphrase", CancellationToken.None);

        Assert.Equal(LocalCredentialSetupResult.AlreadyConfigured, result);
        Assert.Equal(storedAfterFirstSetup, repository.Stored);
        Assert.True(await service.VerifyAsync("correct horse battery staple", CancellationToken.None));
        Assert.False(await service.VerifyAsync("a different passphrase", CancellationToken.None));
    }

    [Fact]
    public async Task Setup_reports_already_configured_when_the_insert_loses_a_concurrent_race_even_though_a_prior_read_reported_no_credential()
    {
        // Simulates the real bug this regression test exists for: two concurrent first-run callers
        // both call GetAsync and both see null before either has written. SetupAsync's own early
        // GetAsync check cannot catch this -- it also sees null -- so the write itself,
        // TryAddAsync, has to be what refuses the loser. This fake's GetAsync always reports "no
        // credential yet" while TryAddAsync always reports "someone else already holds it".
        var service = new LocalCredentialService(new RaceLosingLocalCredentialRepository(), NullLogger<LocalCredentialService>.Instance);

        var result = await service.SetupAsync("correct horse battery staple", CancellationToken.None);

        Assert.Equal(LocalCredentialSetupResult.AlreadyConfigured, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    public async Task Setup_rejects_a_passphrase_below_the_minimum_length(string passphrase)
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository(), NullLogger<LocalCredentialService>.Instance);

        var result = await service.SetupAsync(passphrase, CancellationToken.None);

        Assert.Equal(LocalCredentialSetupResult.TooShort, result);
        Assert.True(await service.IsSetupRequiredAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Setup_rejects_a_passphrase_padded_with_leading_or_trailing_whitespace()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository(), NullLogger<LocalCredentialService>.Instance);

        // "a" plus eleven trailing spaces satisfies the minimum length but carries almost no real
        // entropy once trimmed -- this must be refused rather than silently trimmed and hashed.
        var result = await service.SetupAsync("a" + new string(' ', 11), CancellationToken.None);

        Assert.Equal(LocalCredentialSetupResult.Padded, result);
        Assert.True(await service.IsSetupRequiredAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Setup_accepts_a_passphrase_with_internal_whitespace_that_is_not_leading_or_trailing()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository(), NullLogger<LocalCredentialService>.Instance);

        var result = await service.SetupAsync("correct horse battery staple", CancellationToken.None);

        Assert.Equal(LocalCredentialSetupResult.Configured, result);
    }

    [Fact]
    public async Task Setup_rejects_a_passphrase_over_the_maximum_length()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository(), NullLogger<LocalCredentialService>.Instance);

        var result = await service.SetupAsync(
            new string('a', LocalCredentialService.MaximumPassphraseLength + 1),
            CancellationToken.None);

        Assert.Equal(LocalCredentialSetupResult.TooLong, result);
        Assert.True(await service.IsSetupRequiredAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Setup_accepts_a_passphrase_exactly_at_the_maximum_length()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository(), NullLogger<LocalCredentialService>.Instance);

        var result = await service.SetupAsync(
            new string('a', LocalCredentialService.MaximumPassphraseLength),
            CancellationToken.None);

        Assert.Equal(LocalCredentialSetupResult.Configured, result);
    }

    [Fact]
    public async Task Verify_accepts_the_configured_passphrase_and_rejects_others()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository(), NullLogger<LocalCredentialService>.Instance);
        await service.SetupAsync("correct horse battery staple", CancellationToken.None);

        Assert.True(await service.VerifyAsync("correct horse battery staple", CancellationToken.None));
        Assert.False(await service.VerifyAsync("wrong passphrase entirely", CancellationToken.None));
    }

    [Fact]
    public async Task Verify_fails_when_no_credential_has_been_configured()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository(), NullLogger<LocalCredentialService>.Instance);

        Assert.False(await service.VerifyAsync("anything at all", CancellationToken.None));
    }

    [Fact]
    public async Task Verify_fails_for_a_null_passphrase()
    {
        var service = new LocalCredentialService(new InMemoryLocalCredentialRepository(), NullLogger<LocalCredentialService>.Instance);
        await service.SetupAsync("correct horse battery staple", CancellationToken.None);

        Assert.False(await service.VerifyAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Verify_fails_closed_when_the_stored_hash_is_not_valid_base64()
    {
        var repository = new InMemoryLocalCredentialRepository();
        await repository.SetAsync("not-base64!!", CancellationToken.None);
        var service = new LocalCredentialService(repository, NullLogger<LocalCredentialService>.Instance);

        Assert.False(await service.VerifyAsync("correct horse battery staple", CancellationToken.None));
    }

    [Fact]
    public async Task The_stored_value_is_not_the_passphrase()
    {
        var repository = new InMemoryLocalCredentialRepository();
        var service = new LocalCredentialService(repository, NullLogger<LocalCredentialService>.Instance);

        await service.SetupAsync("correct horse battery staple", CancellationToken.None);

        Assert.NotNull(repository.Stored);
        Assert.DoesNotContain("correct horse", repository.Stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_setup_stores_a_distinct_salted_hash()
    {
        var first = new InMemoryLocalCredentialRepository();
        var second = new InMemoryLocalCredentialRepository();
        await new LocalCredentialService(first, NullLogger<LocalCredentialService>.Instance)
            .SetupAsync("correct horse battery staple", CancellationToken.None);
        await new LocalCredentialService(second, NullLogger<LocalCredentialService>.Instance)
            .SetupAsync("correct horse battery staple", CancellationToken.None);

        Assert.NotEqual(first.Stored, second.Stored);
    }

    [Fact]
    public async Task Verify_upgrades_a_hash_written_with_weaker_settings()
    {
        var legacy = new PasswordHasher<object>(
            Options.Create(new PasswordHasherOptions { IterationCount = 1000 }));
        var repository = new InMemoryLocalCredentialRepository();
        await repository.SetAsync(legacy.HashPassword(new object(), "correct horse battery staple"), CancellationToken.None);
        var before = repository.Stored;
        var service = new LocalCredentialService(repository, NullLogger<LocalCredentialService>.Instance);

        Assert.True(await service.VerifyAsync("correct horse battery staple", CancellationToken.None));
        Assert.NotEqual(before, repository.Stored);
        Assert.True(await service.VerifyAsync("correct horse battery staple", CancellationToken.None));
    }

    [Fact]
    public async Task Verify_succeeds_even_when_the_rehash_write_fails()
    {
        var legacy = new PasswordHasher<object>(
            Options.Create(new PasswordHasherOptions { IterationCount = 1000 }));
        var repository = new ThrowOnSetLocalCredentialRepository(
            legacy.HashPassword(new object(), "correct horse battery staple"));
        var service = new LocalCredentialService(repository, NullLogger<LocalCredentialService>.Instance);

        Assert.True(await service.VerifyAsync("correct horse battery staple", CancellationToken.None));
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

        public Task<bool> TryAddAsync(string passwordHash, CancellationToken cancellationToken)
        {
            if (Stored is not null)
            {
                return Task.FromResult(false);
            }

            Stored = passwordHash;
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// A stored hash that never changes because every write fails -- used to prove that a rehash
    /// write failure does not turn a correct passphrase into a denied login.
    /// </summary>
    private sealed class ThrowOnSetLocalCredentialRepository(string initialHash) : ILocalCredentialRepository
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(initialHash);

        public Task SetAsync(string passwordHash, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated write failure.");

        public Task<bool> TryAddAsync(string passwordHash, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by these tests -- this fake only exercises the VerifyAsync rehash path.");
    }

    /// <summary>
    /// Simulates the loser of a true concurrent first-run setup race: <see cref="GetAsync"/> always
    /// reports no credential exists (the stale read both concurrent callers would have made), while
    /// <see cref="TryAddAsync"/> always reports conflict (the write itself is authoritative).
    /// </summary>
    private sealed class RaceLosingLocalCredentialRepository : ILocalCredentialRepository
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<bool> TryAddAsync(string passwordHash, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task SetAsync(string passwordHash, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by this test.");
    }
}
