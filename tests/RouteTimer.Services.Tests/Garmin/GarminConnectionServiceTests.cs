using System.Text;
using RouteTimer.Services.Garmin;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Tests.Garmin;

public sealed class GarminConnectionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 30, 0, TimeSpan.Zero);

    // Break caught: a successful login could persist or return plaintext Garmin credentials or tokens.
    [Fact]
    public async Task Login_saves_only_protected_tokens_and_returns_safe_identity()
    {
        var adapter = new FakeAdapterClient
        {
            LoginResult = new GarminAdapterLogin("connected", null, "token-json", "42", "Jamie")
        };
        var repository = new FakeConnectionRepository();
        using var protector = Protector();
        var service = Service(adapter, repository, protector);

        var result = await service.LoginAsync("rider@example.com", "secret", CancellationToken.None);

        Assert.Equal(new GarminConnectionResult("connected", "42", "Jamie", null), result);
        Assert.NotNull(repository.Current);
        Assert.Equal("connected", repository.Current.State);
        Assert.NotEqual("token-json", Encoding.UTF8.GetString(repository.Current.Token.Ciphertext));
        Assert.Equal("token-json", protector.Unprotect(repository.Current.Token));
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("rider@example.com", result.ToString(), StringComparison.Ordinal);
        Assert.Equal(Now, repository.Current.LastValidatedAt);
        Assert.Equal(Now, repository.Current.UpdatedAt);
    }

    [Fact]
    public async Task Login_returns_only_an_opaque_challenge_when_mfa_is_required()
    {
        var adapter = new FakeAdapterClient
        {
            LoginResult = new GarminAdapterLogin("mfa-required", "challenge-123", null, null, null)
        };
        var repository = new FakeConnectionRepository();
        using var protector = Protector();
        var service = Service(adapter, repository, protector);

        var result = await service.LoginAsync("rider@example.com", "secret", CancellationToken.None);

        Assert.Equal(new GarminConnectionResult("mfa-required", null, null, "challenge-123"), result);
        Assert.Null(repository.Current);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("   ", "password")]
    [InlineData("rider@example.com", "")]
    [InlineData("rider@example.com", "  ")]
    public async Task Login_rejects_empty_credentials_without_forwarding_or_echoing_them(string email, string password)
    {
        var adapter = new FakeAdapterClient();
        var repository = new FakeConnectionRepository();
        using var protector = Protector();
        var service = Service(adapter, repository, protector);

        var exception = await Assert.ThrowsAsync<GarminCredentialsRejectedException>(
            () => service.LoginAsync(email, password, CancellationToken.None));

        Assert.Equal("Garmin email and password are required.", exception.Message);
        Assert.Equal(0, adapter.LoginCalls);
    }

    [Fact]
    public async Task CompleteMfa_saves_the_protected_token_and_returns_only_safe_identity()
    {
        var adapter = new FakeAdapterClient
        {
            MfaResult = new GarminAdapterLogin("connected", null, "mfa-token-json", "42", "Jamie")
        };
        var repository = new FakeConnectionRepository();
        using var protector = Protector();
        var service = Service(adapter, repository, protector);

        var result = await service.CompleteMfaAsync("challenge-123", "123456", CancellationToken.None);

        Assert.Equal(new GarminConnectionResult("connected", "42", "Jamie", null), result);
        Assert.NotNull(repository.Current);
        Assert.Equal("mfa-token-json", protector.Unprotect(repository.Current.Token));
        Assert.DoesNotContain("123456", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "123456", typeof(GarminChallengeExpiredException), "A Garmin MFA challenge is required.")]
    [InlineData("  ", "123456", typeof(GarminChallengeExpiredException), "A Garmin MFA challenge is required.")]
    [InlineData("challenge-123", "", typeof(GarminMfaInvalidException), "A Garmin MFA code is required.")]
    [InlineData("challenge-123", "  ", typeof(GarminMfaInvalidException), "A Garmin MFA code is required.")]
    public async Task CompleteMfa_rejects_empty_values_without_forwarding_or_echoing_them(
        string challengeId,
        string code,
        Type exceptionType,
        string expectedMessage)
    {
        var adapter = new FakeAdapterClient();
        var repository = new FakeConnectionRepository();
        using var protector = Protector();
        var service = Service(adapter, repository, protector);

        var exception = await Assert.ThrowsAsync(exceptionType,
            () => service.CompleteMfaAsync(challengeId, code, CancellationToken.None));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Equal(0, adapter.MfaCalls);
        if (code.Length > 0)
        {
            Assert.DoesNotContain(code, exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("connected")]
    [InlineData("mfa-required")]
    public async Task Connected_authentication_rejects_an_unusable_adapter_result(string operation)
    {
        var adapter = new FakeAdapterClient
        {
            LoginResult = new GarminAdapterLogin(operation, operation == "mfa-required" ? " " : null, null, null, null),
            MfaResult = new GarminAdapterLogin(operation, operation == "mfa-required" ? " " : null, null, null, null)
        };
        var repository = new FakeConnectionRepository();
        using var protector = Protector();
        var service = Service(adapter, repository, protector);

        var action = operation == "connected"
            ? service.LoginAsync("rider@example.com", "secret", CancellationToken.None)
            : service.CompleteMfaAsync("challenge-123", "123456", CancellationToken.None);

        await Assert.ThrowsAsync<GarminResponseInvalidException>(() => action);
        Assert.Null(repository.Current);
    }

    [Fact]
    public async Task Validate_returns_disconnected_without_decrypting_or_contacting_the_adapter()
    {
        var adapter = new FakeAdapterClient();
        var repository = new FakeConnectionRepository();
        var protector = new TrackingTokenProtector();
        var service = Service(adapter, repository, protector);

        var result = await service.ValidateAsync(CancellationToken.None);

        Assert.Equal(new GarminConnectionResult("disconnected", null, null, null), result);
        Assert.Equal(0, protector.UnprotectCalls);
        Assert.Equal(0, adapter.ValidateCalls);
    }

    [Fact]
    public async Task Validate_returns_reconnect_required_without_decrypting_or_contacting_the_adapter()
    {
        var adapter = new FakeAdapterClient();
        var repository = new FakeConnectionRepository { Current = Connection("reconnect-required") };
        var protector = new TrackingTokenProtector();
        var service = Service(adapter, repository, protector);

        var result = await service.ValidateAsync(CancellationToken.None);

        Assert.Equal(new GarminConnectionResult("reconnect-required", "42", "Jamie", null), result);
        Assert.Equal(0, protector.UnprotectCalls);
        Assert.Equal(0, adapter.ValidateCalls);
    }

    [Fact]
    public async Task Validate_persists_the_rotated_token_and_refreshed_safe_identity()
    {
        var adapter = new FakeAdapterClient
        {
            ValidateResult = new GarminAdapterSession("rotated-token", "84", "Jay")
        };
        using var protector = Protector();
        var repository = new FakeConnectionRepository { Current = Connection("connected", protector.Protect("saved-token")) };
        var service = Service(adapter, repository, protector);

        var result = await service.ValidateAsync(CancellationToken.None);

        Assert.Equal(new GarminConnectionResult("connected", "84", "Jay", null), result);
        Assert.Equal("saved-token", adapter.LastValidatedToken);
        Assert.NotNull(repository.Current);
        Assert.Equal("rotated-token", protector.Unprotect(repository.Current.Token));
        Assert.Equal(Now, repository.Current.LastValidatedAt);
        Assert.Equal(Now, repository.Current.UpdatedAt);
    }

    [Fact]
    public async Task Deterministic_validation_failure_marks_the_saved_connection_reconnect_required()
    {
        using var protector = Protector();
        var original = Connection("connected", protector.Protect("saved-token"));
        var adapter = new FakeAdapterClient
        {
            ValidateException = new GarminAdapterException(GarminAdapterError.Authentication, "token detail must stay private")
        };
        var repository = new FakeConnectionRepository { Current = original };
        var service = Service(adapter, repository, protector);

        var exception = await Assert.ThrowsAsync<GarminReconnectRequiredException>(
            () => service.ValidateAsync(CancellationToken.None));

        Assert.Equal("The Garmin connection must be established again.", exception.Message);
        Assert.NotNull(repository.Current);
        Assert.Equal("reconnect-required", repository.Current.State);
        Assert.Same(original.Token, repository.Current.Token);
        Assert.Equal(original.LastValidatedAt, repository.Current.LastValidatedAt);
        Assert.Equal(Now, repository.Current.UpdatedAt);
    }

    [Theory]
    [InlineData(GarminAdapterError.RateLimited)]
    [InlineData(GarminAdapterError.Unavailable)]
    [InlineData(GarminAdapterError.AdapterUnavailable)]
    public async Task Transient_validation_failure_preserves_the_connected_state_and_token(GarminAdapterError error)
    {
        using var protector = Protector();
        var original = Connection("connected", protector.Protect("saved-token"));
        var adapter = new FakeAdapterClient
        {
            ValidateException = new GarminAdapterException(error, "private adapter detail")
        };
        var repository = new FakeConnectionRepository { Current = original };
        var service = Service(adapter, repository, protector);

        var exception = await Assert.ThrowsAsync<GarminAdapterException>(
            () => service.ValidateAsync(CancellationToken.None));

        Assert.Equal(error, exception.Error);
        Assert.Same(original, repository.Current);
        Assert.Equal(0, repository.SaveCalls);
    }

    [Fact]
    public async Task Authentication_operations_are_serialized_through_the_shared_gate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var adapter = new FakeAdapterClient
        {
            LoginResult = new GarminAdapterLogin("mfa-required", "challenge-1", null, null, null),
            MfaResult = new GarminAdapterLogin("connected", null, "token-json", "42", "Jamie"),
            LoginEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ReleaseLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            MfaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var repository = new FakeConnectionRepository();
        using var protector = Protector();
        var service = Service(adapter, repository, protector);

        var login = service.LoginAsync("rider@example.com", "secret", timeout.Token);
        await adapter.LoginEntered.Task.WaitAsync(timeout.Token);
        var mfa = service.CompleteMfaAsync("challenge-1", "123456", timeout.Token);
        await Task.Yield();

        Assert.False(adapter.MfaEntered.Task.IsCompleted);
        adapter.ReleaseLogin.SetResult();
        await login;
        await adapter.MfaEntered.Task.WaitAsync(timeout.Token);
        await mfa;
    }

    [Fact]
    public async Task Validation_and_disconnect_are_serialized_through_the_shared_gate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var protector = Protector();
        var adapter = new FakeAdapterClient
        {
            ValidateResult = new GarminAdapterSession("rotated-token", "42", "Jamie"),
            ValidateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ReleaseValidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ClearEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var repository = new FakeConnectionRepository { Current = Connection("connected", protector.Protect("saved-token")) };
        var service = Service(adapter, repository, protector);

        var validate = service.ValidateAsync(timeout.Token);
        await adapter.ValidateEntered.Task.WaitAsync(timeout.Token);
        var disconnect = service.DisconnectAsync(timeout.Token);
        await Task.Yield();

        Assert.False(adapter.ClearEntered.Task.IsCompleted);
        Assert.Equal(0, repository.DeleteCalls);
        adapter.ReleaseValidate.SetResult();
        await validate;
        await adapter.ClearEntered.Task.WaitAsync(timeout.Token);
        await disconnect;
        Assert.Equal(1, repository.DeleteCalls);
    }

    [Fact]
    public async Task Disconnect_clears_challenges_and_deletes_an_existing_connection()
    {
        var adapter = new FakeAdapterClient();
        var repository = new FakeConnectionRepository { Current = Connection("connected") };
        using var protector = Protector();
        var service = Service(adapter, repository, protector);

        await service.DisconnectAsync(CancellationToken.None);

        Assert.Equal(1, adapter.ClearCalls);
        Assert.Equal(1, repository.DeleteCalls);
        Assert.Null(repository.Current);
    }

    [Fact]
    public async Task Disconnect_always_deletes_the_connection_when_clearing_challenges_fails()
    {
        var adapter = new FakeAdapterClient
        {
            ClearException = new GarminAdapterException(GarminAdapterError.AdapterUnavailable, "internal adapter URL")
        };
        var repository = new FakeConnectionRepository { Current = Connection("connected") };
        using var protector = Protector();
        var service = Service(adapter, repository, protector);

        await service.DisconnectAsync(CancellationToken.None);

        Assert.Equal(1, adapter.ClearCalls);
        Assert.Equal(1, repository.DeleteCalls);
        Assert.Null(repository.Current);
    }

    [Fact]
    public async Task Disconnect_is_idempotent_when_no_connection_exists()
    {
        var adapter = new FakeAdapterClient();
        var repository = new FakeConnectionRepository();
        using var protector = Protector();
        var service = Service(adapter, repository, protector);

        await service.DisconnectAsync(CancellationToken.None);
        await service.DisconnectAsync(CancellationToken.None);

        Assert.Equal(2, adapter.ClearCalls);
        Assert.Equal(2, repository.DeleteCalls);
        Assert.Null(repository.Current);
    }

    private static GarminConnectionService Service(
        IGarminAdapterClient adapter,
        IGarminConnectionRepository repository,
        IGarminTokenProtector protector) =>
        new(adapter, repository, protector, new GarminOperationGate(), new FixedTimeProvider(Now));

    private static AesGcmGarminTokenProtector Protector() =>
        new(Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

    private static GarminConnectionRecord Connection(string state, ProtectedGarminToken? token = null) =>
        new(
            state,
            "42",
            "Jamie",
            token ?? new ProtectedGarminToken(1, new byte[12], [1], new byte[16]),
            Now.AddHours(-1),
            Now.AddMinutes(-5));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TrackingTokenProtector : IGarminTokenProtector
    {
        public int UnprotectCalls { get; private set; }

        public ProtectedGarminToken Protect(string tokenJson) =>
            new(1, new byte[12], Encoding.UTF8.GetBytes(tokenJson), new byte[16]);

        public string Unprotect(ProtectedGarminToken protectedToken)
        {
            UnprotectCalls++;
            return Encoding.UTF8.GetString(protectedToken.Ciphertext);
        }
    }

    private sealed class FakeConnectionRepository : IGarminConnectionRepository
    {
        public GarminConnectionRecord? Current { get; set; }
        public int SaveCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public Task<GarminConnectionRecord?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        public Task SaveAsync(GarminConnectionRecord connection, CancellationToken cancellationToken)
        {
            SaveCalls++;
            Current = connection;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            DeleteCalls++;
            Current = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdapterClient : IGarminAdapterClient
    {
        public GarminAdapterLogin LoginResult { get; set; } = new("connected", null, "token-json", "42", "Jamie");
        public GarminAdapterLogin MfaResult { get; set; } = new("connected", null, "token-json", "42", "Jamie");
        public GarminAdapterSession ValidateResult { get; set; } = new("token-json", "42", "Jamie");
        public GarminAdapterException? ValidateException { get; set; }
        public Exception? ClearException { get; set; }
        public TaskCompletionSource? LoginEntered { get; set; }
        public TaskCompletionSource? ReleaseLogin { get; set; }
        public TaskCompletionSource? MfaEntered { get; set; }
        public TaskCompletionSource? ValidateEntered { get; set; }
        public TaskCompletionSource? ReleaseValidate { get; set; }
        public TaskCompletionSource? ClearEntered { get; set; }
        public int LoginCalls { get; private set; }
        public int MfaCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public int ClearCalls { get; private set; }
        public string? LastValidatedToken { get; private set; }

        public async Task<GarminAdapterLogin> LoginAsync(string email, string password, CancellationToken cancellationToken)
        {
            LoginCalls++;
            LoginEntered?.SetResult();
            if (ReleaseLogin is not null)
            {
                await ReleaseLogin.Task.WaitAsync(cancellationToken);
            }

            return LoginResult;
        }

        public Task<GarminAdapterLogin> CompleteMfaAsync(string challengeId, string code, CancellationToken cancellationToken)
        {
            MfaCalls++;
            MfaEntered?.SetResult();
            return Task.FromResult(MfaResult);
        }

        public async Task<GarminAdapterSession> ValidateAsync(string tokenJson, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            LastValidatedToken = tokenJson;
            ValidateEntered?.SetResult();
            if (ReleaseValidate is not null)
            {
                await ReleaseValidate.Task.WaitAsync(cancellationToken);
            }

            if (ValidateException is not null)
            {
                throw ValidateException;
            }

            return ValidateResult;
        }

        public Task<GarminAdapterActivityPage> GetActivitiesAsync(string tokenJson, int offset, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GarminAdapterActivityResult> GetActivityAsync(string tokenJson, string activityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GarminAdapterFitDownload> DownloadFitAsync(string tokenJson, string activityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ClearChallengesAsync(CancellationToken cancellationToken)
        {
            ClearCalls++;
            ClearEntered?.SetResult();
            return ClearException is null ? Task.CompletedTask : Task.FromException(ClearException);
        }
    }
}
