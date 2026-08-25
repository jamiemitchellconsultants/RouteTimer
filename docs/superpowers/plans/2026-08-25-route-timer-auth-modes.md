# RouteTimer Authentication Modes and Runtime Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make one RouteTimer image run in either an app-native local credential mode or the existing Keycloak mode, selected at runtime rather than baked in at build time.

**Architecture:** `Auth:Mode` is a required setting with two values. The existing authorization policy — authenticated user holding the `rider` role — is untouched; only the authentication scheme producing that principal differs. `Local` mode adds a cookie scheme backed by a single-row credential table. The WebAssembly client stops reading build-time settings and instead fetches `GET /api/auth/config` before building its host, then registers either OIDC or cookie authentication. A migration-state readiness check closes a gap where readiness reported healthy before migrations finished.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core with Npgsql, Blazor WebAssembly, xUnit, bUnit 2.9, Testcontainers for PostgreSQL.

**Source spec:** `docs/superpowers/specs/2026-08-25-route-timer-deployment-design.md`

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `src/RouteTimer.Api/Auth/AuthMode.cs` | The two-value enum and the configuration resolver that fails fast |
| `src/RouteTimer.Api/Auth/LocalCredentialService.cs` | Hash, verify, and set-once semantics for the local passphrase |
| `src/RouteTimer.Api/Auth/LocalAuthenticationDefaults.cs` | Cookie scheme name and cookie configuration constants |
| `src/RouteTimer.Api/Endpoints/AuthEndpoints.cs` | `/api/auth/config`, `/api/auth/session`, `/api/auth/setup`, `/api/auth/login`, `/api/auth/logout` |
| `src/RouteTimer.Api/Health/MigrationsReadyHealthCheck.cs` | Reports unhealthy until migrations have completed |
| `src/RouteTimer.Api/Health/MigrationState.cs` | Singleton flag the migration service sets and the health check reads |
| `src/RouteTimer.Contracts/Auth/AuthContracts.cs` | `AuthConfigResponse`, `AuthSessionResponse`, `SetLocalCredentialRequest`, `LocalLoginRequest` |
| `src/RouteTimer.Services/Persistence/ILocalCredentialRepository.cs` | Repository interface, alongside the existing ones |
| `src/RouteTimer.Persistence/Entities/LocalCredentialEntity.cs` | The single-row credential entity |
| `src/RouteTimer.Persistence/Repositories/LocalCredentialRepository.cs` | EF Core implementation |
| `src/RouteTimer.Client/Auth/LocalAuthenticationStateProvider.cs` | Client auth state backed by `/api/auth/session` |
| `src/RouteTimer.Client/Auth/ClientAuthConfig.cs` | Deserialised shape of `/api/auth/config` on the client |
| `src/RouteTimer.Client/Pages/LocalSignIn.razor` | First-run setup and sign-in page for local mode |
| `tests/RouteTimer.Api.Tests/Auth/AuthModeTests.cs` | Mode resolution and startup failure |
| `tests/RouteTimer.Api.Tests/Auth/LocalAuthEndpointTests.cs` | Setup, login, logout, lockout |
| `tests/RouteTimer.Api.Tests/Auth/AuthConfigEndpointTests.cs` | Config and session payloads in both modes |
| `tests/RouteTimer.Api.Tests/MigrationsReadinessTests.cs` | Readiness gating on migration state |
| `tests/RouteTimer.Persistence.Tests/LocalCredentialRepositoryTests.cs` | Repository round trip against real PostgreSQL |
| `tests/RouteTimer.Client.Tests/Auth/LocalSignInPageTests.cs` | Setup and sign-in page behaviour |

**Modified:**

| File | Change |
|---|---|
| `src/RouteTimer.Api/Program.cs` | Branch authentication registration on `Auth:Mode`; register the new services, endpoints, rate limiter and health check |
| `src/RouteTimer.Api/DatabaseMigrationService.cs` | Set the migration-complete flag |
| `src/RouteTimer.Persistence/RouteTimerDbContext.cs` | Map `local_credential` |
| `src/RouteTimer.Client/Program.cs` | Fetch auth config before `Build()`; register OIDC or cookie auth accordingly |
| `src/RouteTimer.Client/RedirectToLogin.razor` | Send local-mode users to the local sign-in page instead of the OIDC flow |
| `src/RouteTimer.Client/Pages/{Home,Profile,Training,TrainingDetail,Predictions,PredictionDetail}.razor` | Add `[Authorize]` so `AuthorizeRouteView` actually gates them |
| `src/RouteTimer.Client/wwwroot/appsettings.json` | Remove the `Keycloak` section |
| `Dockerfile` | Remove the auth build arguments and the generated `appsettings.Production.json` |
| `tests/RouteTimer.Api.Tests/RouteTimerApiFactory.cs` | Supply `Auth:Mode`; add a helper to select it |
| `tests/RouteTimer.Api.Tests/HealthEndpointTests.cs` | Supply `Auth:Mode` to its three bare factories |

**Boundary note:** `RouteTimer.Services` and `RouteTimer.Domain` must not reference ASP.NET Core. `PasswordHasher<T>` is an ASP.NET Core shared-framework type, so `LocalCredentialService` lives in `RouteTimer.Api/Auth/`, not in Services. Only the repository *interface* goes in Services, matching where `IProfileRepository` already lives.

---

## Task 1: Auth mode resolution and startup failure

**Files:**
- Create: `src/RouteTimer.Api/Auth/AuthMode.cs`
- Create: `tests/RouteTimer.Api.Tests/Auth/AuthModeTests.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `tests/RouteTimer.Api.Tests/RouteTimerApiFactory.cs`
- Modify: `tests/RouteTimer.Api.Tests/HealthEndpointTests.cs`
- Modify: `src/RouteTimer.Api/appsettings.Development.json`

This task will break every existing API test unless the factories are updated in the same commit. Do not split it.

- [ ] **Step 1: Write the failing test**

Create `tests/RouteTimer.Api.Tests/Auth/AuthModeTests.cs`:

```csharp
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

    private static IConfiguration Build(string? configured) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Mode"] = configured })
            .Build();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~AuthModeTests"`

Expected: FAIL to compile with `CS0246: The type or namespace name 'AuthMode' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/RouteTimer.Api/Auth/AuthMode.cs`:

```csharp
namespace RouteTimer.Api.Auth;

/// <summary>How a request is authenticated. Selected per deployment; there is no default.</summary>
public enum AuthMode
{
    /// <summary>Single-rider passphrase held by this deployment, used for local installations.</summary>
    Local,

    /// <summary>Bearer tokens issued by an external Keycloak realm.</summary>
    Keycloak
}

public static class AuthModeResolver
{
    public const string ConfigurationKey = "Auth:Mode";

    /// <summary>
    /// Reads the deployment's authentication mode. There is deliberately no default: a deployment
    /// that does not state what it is must not start, because guessing wrong in either direction is
    /// worse than refusing to run.
    /// </summary>
    public static AuthMode Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Matched by name rather than parsed. Enum.TryParse would accept "0" and "1" -- which are
        // defined values, so Enum.IsDefined does not catch them -- and it bitwise-ORs comma-separated
        // lists on any enum, Flags or not. Either would silently pick a mode the operator did not ask
        // for, which is the exact failure this setting exists to prevent.
        var configured = configuration[ConfigurationKey]?.Trim();
        if (string.Equals(configured, nameof(AuthMode.Local), StringComparison.OrdinalIgnoreCase))
        {
            return AuthMode.Local;
        }

        if (string.Equals(configured, nameof(AuthMode.Keycloak), StringComparison.OrdinalIgnoreCase))
        {
            return AuthMode.Keycloak;
        }

        throw new InvalidOperationException(
            $"{ConfigurationKey} must be set to either 'Local' or 'Keycloak'. " +
            $"The configured value was {(string.IsNullOrWhiteSpace(configured) ? "not set" : $"'{configured}'")}. " +
            "Local mode authenticates with a passphrase set on first use; Keycloak mode authenticates " +
            "bearer tokens from the authority in Keycloak:Authority.");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~AuthModeTests"`

Expected: PASS, 13 tests.

- [ ] **Step 5: Call the resolver from Program.cs**

In `src/RouteTimer.Api/Program.cs`, add to the using block at the top:

```csharp
using RouteTimer.Api.Auth;
```

Replace the existing authentication registration block — the lines from `builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)` through the closing `});` of `AddJwtBearer` — with:

```csharp
var authMode = AuthModeResolver.Resolve(builder.Configuration);
if (authMode == AuthMode.Keycloak)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = builder.Configuration["Keycloak:Authority"];
            options.Audience = "routetimer-api";
            options.RequireHttpsMetadata = true;
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    KeycloakRealmRoleMapper.AddRealmRoles(context.Principal);
                    return Task.CompletedTask;
                }
            };
        });
}
```

Local mode's authentication registration is added in Task 5. Until then, local mode registers no scheme and its requests are rejected, which is correct and temporary.

- [ ] **Step 6: Supply the mode from the API test factory**

In `tests/RouteTimer.Api.Tests/RouteTimerApiFactory.cs`, change the class declaration and add the mode:

```csharp
public sealed class RouteTimerApiFactory(
    bool authenticateAsRider = false,
    Action<IServiceCollection>? configureServices = null,
    string authMode = "Keycloak")
    : WebApplicationFactory<Program>
{
    private readonly string databaseName = Guid.NewGuid().ToString();

    public RouteTimerApiFactory WithRiderAuthentication(Action<IServiceCollection>? configure = null) =>
        new(true, Combine(configureServices, configure), authMode);

    public RouteTimerApiFactory WithServices(Action<IServiceCollection> configure) =>
        new(authenticateAsRider, Combine(configureServices, configure), authMode);

    public RouteTimerApiFactory WithAuthMode(string mode) =>
        new(authenticateAsRider, configureServices, mode);
```

Then, at the very start of `ConfigureWebHost`, before `builder.ConfigureTestServices(...)`:

```csharp
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(RouteTimer.Api.Auth.AuthModeResolver.ConfigurationKey, authMode);

        builder.ConfigureTestServices(services =>
        {
```

Leave the rest of the method unchanged.

- [ ] **Step 7: Supply the mode to the three bare factories in HealthEndpointTests**

In `tests/RouteTimer.Api.Tests/HealthEndpointTests.cs`, the two `new WebApplicationFactory<Program>()` uses and the `ReadyHealthApplicationFactory` all boot the real `Program` and will now throw. Add this private class at the end of the test class, immediately before `ReadyHealthApplicationFactory`:

```csharp
    private class KeycloakModeApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting(RouteTimer.Api.Auth.AuthModeResolver.ConfigurationKey, "Keycloak");
    }
```

Change `ReadyHealthApplicationFactory` to derive from it and call the base:

```csharp
    private sealed class ReadyHealthApplicationFactory : KeycloakModeApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<RouteTimerDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<RouteTimerDbContext>>();
                services.AddDbContext<RouteTimerDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            });
        }
    }
```

In `Live_health_is_anonymous_and_returns_healthy`, replace:

```csharp
        await using var app = new WebApplicationFactory<Program>();
```

with:

```csharp
        await using var app = new KeycloakModeApplicationFactory();
```

In `Application_services_resolve_the_complete_build_model_handler_graph`, replace:

```csharp
        using var app = new WebApplicationFactory<Program>();
```

with:

```csharp
        using var app = new KeycloakModeApplicationFactory();
```

- [ ] **Step 8: Add an integration test that startup fails without the mode**

Append to `tests/RouteTimer.Api.Tests/Auth/AuthModeTests.cs`, inside the class:

```csharp
    [Fact]
    public void The_application_refuses_to_start_without_an_authentication_mode()
    {
        using var app = new RouteTimerApiFactory().WithAuthMode(string.Empty);

        var exception = Assert.ThrowsAny<InvalidOperationException>(() => app.CreateClient());

        Assert.Contains("Auth:Mode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_application_starts_in_local_mode()
    {
        await using var app = new RouteTimerApiFactory().WithAuthMode("Local");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
```

Add `using System.Net;` to the file. Local mode is the only behavioural branch this task
introduces and the one Task 5 builds on, so it needs a test that the application at least boots.
Do not assert anything about protected endpoints in local mode yet: with no scheme registered they
return 500, and Task 5 changes that to 401.

- [ ] **Step 9: Keep `dotnet run` working**

Nothing else in the repository sets `Auth:Mode`, so from this commit a developer pressing F5 or
running `dotnet run` hits the startup exception. Add the `Auth` section to
`src/RouteTimer.Api/appsettings.Development.json`, keeping the existing `Logging` section:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Auth": {
    "Mode": "Local"
  }
}
```

This file loads only when `ASPNETCORE_ENVIRONMENT=Development`, so no deployment inherits it and the
fail-fast guarantee is untouched. `Local` is the right development default because it runs
standalone without an external Keycloak once Task 5 lands.

- [ ] **Step 10: Run the full API suite**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false`

Expected: PASS. 66 pre-existing tests plus 15 new ones, 81 total, 0 failed. If any pre-existing test fails with a message naming `Auth:Mode`, a factory was missed in steps 6 and 7.

- [ ] **Step 11: Commit**

```bash
git add src/RouteTimer.Api tests/RouteTimer.Api.Tests
git commit -m "feat: require an explicit authentication mode"
```

---

## Task 2: Local credential storage

**Files:**
- Create: `src/RouteTimer.Persistence/Entities/LocalCredentialEntity.cs`
- Create: `src/RouteTimer.Services/Persistence/ILocalCredentialRepository.cs`
- Create: `src/RouteTimer.Persistence/Repositories/LocalCredentialRepository.cs`
- Create: `tests/RouteTimer.Persistence.Tests/LocalCredentialRepositoryTests.cs`
- Modify: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`

- [ ] **Step 1: Write the failing test**

Repository tests in this project build an in-memory context inline rather than sharing a fixture — see `RepositoryRoundTripTests.cs`. Follow that pattern.

Create `tests/RouteTimer.Persistence.Tests/LocalCredentialRepositoryTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;

namespace RouteTimer.Persistence.Tests;

public sealed class LocalCredentialRepositoryTests
{
    [Fact]
    public async Task Get_returns_null_before_a_credential_is_set()
    {
        await using var context = CreateContext();
        var repository = new LocalCredentialRepository(context);

        Assert.Null(await repository.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Set_then_get_round_trips_the_hash()
    {
        await using var context = CreateContext();
        var repository = new LocalCredentialRepository(context);

        await repository.SetAsync("hashed-value", CancellationToken.None);

        Assert.Equal("hashed-value", await repository.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Set_replaces_the_single_row_rather_than_adding_another()
    {
        await using var context = CreateContext();
        var repository = new LocalCredentialRepository(context);

        await repository.SetAsync("first", CancellationToken.None);
        await repository.SetAsync("second", CancellationToken.None);

        Assert.Equal("second", await repository.GetAsync(CancellationToken.None));
        Assert.Equal(1, await context.LocalCredentials.CountAsync(CancellationToken.None));
    }

    private static RouteTimerDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~LocalCredentialRepositoryTests"`

Expected: FAIL to compile with `CS0246` for `LocalCredentialRepository`.

- [ ] **Step 3: Add the repository interface**

Create `src/RouteTimer.Services/Persistence/ILocalCredentialRepository.cs`:

```csharp
namespace RouteTimer.Services.Persistence;

/// <summary>
/// Stores the single local-mode passphrase hash. At most one credential exists; this is a
/// single-rider deployment, not a user store.
/// </summary>
public interface ILocalCredentialRepository
{
    /// <summary>Returns the stored hash, or null when first-run setup has not happened yet.</summary>
    Task<string?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Stores the hash, replacing any existing one.</summary>
    Task SetAsync(string passwordHash, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Add the entity**

Create `src/RouteTimer.Persistence/Entities/LocalCredentialEntity.cs`:

```csharp
namespace RouteTimer.Persistence.Entities;

public sealed class LocalCredentialEntity
{
    public int Id { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 5: Map it in the DbContext**

In `src/RouteTimer.Persistence/RouteTimerDbContext.cs`, add the set alongside the others:

```csharp
    public DbSet<LocalCredentialEntity> LocalCredentials => Set<LocalCredentialEntity>();
```

And at the end of `OnModelCreating`, before its closing brace:

```csharp
        var localCredential = modelBuilder.Entity<LocalCredentialEntity>();
        localCredential.ToTable("local_credential");
        localCredential.HasKey(entity => entity.Id);
        localCredential.Property(entity => entity.PasswordHash).HasMaxLength(256).IsRequired();
        localCredential.Property(entity => entity.CreatedAt).HasColumnType("timestamp with time zone");
        localCredential.Property(entity => entity.UpdatedAt).HasColumnType("timestamp with time zone");
```

- [ ] **Step 6: Add the repository**

Create `src/RouteTimer.Persistence/Repositories/LocalCredentialRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

public sealed class LocalCredentialRepository(RouteTimerDbContext context) : ILocalCredentialRepository
{
    private const int SingletonId = 1;

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        var credential = await context.LocalCredentials.SingleOrDefaultAsync(cancellationToken);
        return credential?.PasswordHash;
    }

    public async Task SetAsync(string passwordHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var now = DateTimeOffset.UtcNow;
        var credential = await context.LocalCredentials.SingleOrDefaultAsync(cancellationToken);
        if (credential is null)
        {
            context.LocalCredentials.Add(new LocalCredentialEntity
            {
                Id = SingletonId,
                PasswordHash = passwordHash,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            credential.PasswordHash = passwordHash;
            credential.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
```

- [ ] **Step 7: Generate the migration**

Run:

```bash
dotnet ef migrations add AddLocalCredential --project src/RouteTimer.Persistence --startup-project src/RouteTimer.Api --output-dir Migrations
```

If `dotnet ef` is not installed, run `dotnet tool install --global dotnet-ef` first. The command needs `Auth:Mode` because it builds the API host; prefix it with `Auth__Mode=Keycloak` on macOS or Linux:

```bash
Auth__Mode=Keycloak dotnet ef migrations add AddLocalCredential --project src/RouteTimer.Persistence --startup-project src/RouteTimer.Api --output-dir Migrations
```

Open the generated `*_AddLocalCredential.cs` and confirm it creates only the `local_credential` table. If it contains any other change, the model snapshot was stale — investigate before continuing rather than editing the migration by hand.

- [ ] **Step 8: Run the persistence tests**

Run: `dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj -p:UseSharedCompilation=false`

Expected: PASS. 143 pre-existing plus 3 new. This suite starts a PostgreSQL container for `PostgresMigrationTests` and takes about 45 seconds overall.

- [ ] **Step 9: Commit**

```bash
git add src/RouteTimer.Persistence src/RouteTimer.Services/Persistence/ILocalCredentialRepository.cs tests/RouteTimer.Persistence.Tests/LocalCredentialRepositoryTests.cs
git commit -m "feat: store the local mode credential"
```

---

## Task 3: Local credential service

**Files:**
- Create: `src/RouteTimer.Api/Auth/LocalCredentialService.cs`
- Create: `tests/RouteTimer.Api.Tests/Auth/LocalCredentialServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/RouteTimer.Api.Tests/Auth/LocalCredentialServiceTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~LocalCredentialServiceTests"`

Expected: FAIL to compile with `CS0246` for `LocalCredentialService`.

- [ ] **Step 3: Write the implementation**

Create `src/RouteTimer.Api/Auth/LocalCredentialService.cs`:

```csharp
using Microsoft.AspNetCore.Identity;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Auth;

public enum LocalCredentialSetupResult
{
    Configured,
    AlreadyConfigured,
    TooShort
}

/// <summary>
/// Owns the local-mode passphrase. Hashing uses the framework's <see cref="PasswordHasher{TUser}"/>,
/// which is available from the ASP.NET Core shared framework and carries its own versioned format,
/// so no hashing scheme is written here.
/// </summary>
public sealed class LocalCredentialService(ILocalCredentialRepository credentials)
{
    /// <summary>
    /// Long enough to resist casual guessing, short enough that a rider will actually use a
    /// passphrase rather than a reused password. Enforced on setup only; existing credentials are
    /// never re-validated against a later change to this value.
    /// </summary>
    public const int MinimumPassphraseLength = 12;

    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object HashSubject = new();

    public async Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken) =>
        await credentials.GetAsync(cancellationToken) is null;

    public async Task<LocalCredentialSetupResult> SetupAsync(string passphrase, CancellationToken cancellationToken)
    {
        if (await credentials.GetAsync(cancellationToken) is not null)
        {
            return LocalCredentialSetupResult.AlreadyConfigured;
        }

        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < MinimumPassphraseLength)
        {
            return LocalCredentialSetupResult.TooShort;
        }

        await credentials.SetAsync(Hasher.HashPassword(HashSubject, passphrase), cancellationToken);
        return LocalCredentialSetupResult.Configured;
    }

    public async Task<bool> VerifyAsync(string passphrase, CancellationToken cancellationToken)
    {
        var storedHash = await credentials.GetAsync(cancellationToken);
        if (storedHash is null || string.IsNullOrEmpty(passphrase))
        {
            return false;
        }

        var outcome = Hasher.VerifyHashedPassword(HashSubject, storedHash, passphrase);
        if (outcome == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await credentials.SetAsync(Hasher.HashPassword(HashSubject, passphrase), cancellationToken);
            return true;
        }

        return outcome == PasswordVerificationResult.Success;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~LocalCredentialServiceTests"`

Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/RouteTimer.Api/Auth/LocalCredentialService.cs tests/RouteTimer.Api.Tests/Auth/LocalCredentialServiceTests.cs
git commit -m "feat: hash and verify the local mode passphrase"
```

---

## Task 4: Auth contracts and the config endpoint

**Files:**
- Create: `src/RouteTimer.Contracts/Auth/AuthContracts.cs`
- Create: `src/RouteTimer.Api/Endpoints/AuthEndpoints.cs`
- Create: `tests/RouteTimer.Api.Tests/Auth/AuthConfigEndpointTests.cs`
- Modify: `src/RouteTimer.Api/Program.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/RouteTimer.Api.Tests/Auth/AuthConfigEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Contracts.Auth;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Auth;

public sealed class AuthConfigEndpointTests
{
    [Fact]
    public async Task Config_is_anonymous_and_reports_keycloak_settings_in_keycloak_mode()
    {
        await using var app = new RouteTimerApiFactory().WithAuthMode("Keycloak");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/auth/config", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var config = await response.Content.ReadFromJsonAsync<AuthConfigResponse>(CancellationToken.None);
        Assert.NotNull(config);
        Assert.Equal("Keycloak", config.Mode);
        Assert.False(config.SetupRequired);
        Assert.Equal("routetimer-web", config.ClientId);
        Assert.Equal("authentication/login-callback", config.RedirectUri);
        Assert.Equal("/", config.PostLogoutRedirectUri);
    }

    [Fact]
    public async Task Config_is_anonymous_and_reports_setup_required_in_local_mode()
    {
        await using var app = new RouteTimerApiFactory()
            .WithAuthMode("Local")
            .WithServices(services =>
            {
                services.RemoveAll<ILocalCredentialRepository>();
                services.AddSingleton<ILocalCredentialRepository>(new FakeLocalCredentialRepository(null));
            });
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/auth/config", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var config = await response.Content.ReadFromJsonAsync<AuthConfigResponse>(CancellationToken.None);
        Assert.NotNull(config);
        Assert.Equal("Local", config.Mode);
        Assert.True(config.SetupRequired);
        Assert.Null(config.Authority);
    }

    [Fact]
    public async Task Config_reports_setup_complete_once_a_credential_exists()
    {
        await using var app = new RouteTimerApiFactory()
            .WithAuthMode("Local")
            .WithServices(services =>
            {
                services.RemoveAll<ILocalCredentialRepository>();
                services.AddSingleton<ILocalCredentialRepository>(new FakeLocalCredentialRepository("a-hash"));
            });
        using var client = app.CreateClient();

        var config = await client.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config", CancellationToken.None);

        Assert.NotNull(config);
        Assert.False(config.SetupRequired);
    }

    [Fact]
    public async Task Session_reports_anonymous_for_an_unauthenticated_caller()
    {
        await using var app = new RouteTimerApiFactory().WithAuthMode("Keycloak");
        using var client = app.CreateClient();

        var session = await client.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session", CancellationToken.None);

        Assert.NotNull(session);
        Assert.False(session.Authenticated);
    }

    internal sealed class FakeLocalCredentialRepository(string? initialHash) : ILocalCredentialRepository
    {
        private string? hash = initialHash;

        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(hash);

        public Task SetAsync(string passwordHash, CancellationToken cancellationToken)
        {
            hash = passwordHash;
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~AuthConfigEndpointTests"`

Expected: FAIL to compile with `CS0246` for `AuthConfigResponse`.

- [ ] **Step 3: Add the contracts**

Create `src/RouteTimer.Contracts/Auth/AuthContracts.cs`:

```csharp
namespace RouteTimer.Contracts.Auth;

/// <summary>
/// The authentication configuration for this deployment, read by the client at startup. This
/// replaces build-time configuration so that one published image serves every deployment.
/// </summary>
/// <param name="Mode">Either "Local" or "Keycloak".</param>
/// <param name="SetupRequired">Local mode only: no passphrase has been set yet.</param>
/// <param name="Authority">Keycloak mode only: the realm's issuer URL.</param>
/// <param name="ClientId">Keycloak mode only: the public SPA client id.</param>
/// <param name="RedirectUri">Keycloak mode only: the login callback path.</param>
/// <param name="PostLogoutRedirectUri">Keycloak mode only: where to land after sign-out.</param>
public sealed record AuthConfigResponse(
    string Mode,
    bool SetupRequired,
    string? Authority,
    string? ClientId,
    string? RedirectUri,
    string? PostLogoutRedirectUri);

/// <param name="Authenticated">Whether the caller currently holds a valid session.</param>
public sealed record AuthSessionResponse(bool Authenticated);

/// <param name="Passphrase">The passphrase to set on first use.</param>
public sealed record SetLocalCredentialRequest(string Passphrase);

/// <param name="Passphrase">The passphrase to sign in with.</param>
public sealed record LocalLoginRequest(string Passphrase);
```

- [ ] **Step 4: Add the endpoints**

Create `src/RouteTimer.Api/Endpoints/AuthEndpoints.cs`:

```csharp
using RouteTimer.Api.Auth;
using RouteTimer.Contracts.Auth;

namespace RouteTimer.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes, AuthMode mode)
    {
        routes.MapGet("/api/auth/config", (IConfiguration configuration, LocalCredentialService credentials, CancellationToken cancellationToken) =>
            GetConfigAsync(mode, configuration, credentials, cancellationToken)).AllowAnonymous();

        routes.MapGet("/api/auth/session", (HttpContext context) =>
            TypedResults.Ok(new AuthSessionResponse(context.User.Identity?.IsAuthenticated == true))).AllowAnonymous();

        return routes;
    }

    private static async Task<IResult> GetConfigAsync(
        AuthMode mode,
        IConfiguration configuration,
        LocalCredentialService credentials,
        CancellationToken cancellationToken)
    {
        if (mode == AuthMode.Local)
        {
            var setupRequired = await credentials.IsSetupRequiredAsync(cancellationToken);
            return TypedResults.Ok(new AuthConfigResponse(
                nameof(AuthMode.Local),
                setupRequired,
                Authority: null,
                ClientId: null,
                RedirectUri: null,
                PostLogoutRedirectUri: null));
        }

        return TypedResults.Ok(new AuthConfigResponse(
            nameof(AuthMode.Keycloak),
            SetupRequired: false,
            Authority: configuration["Keycloak:Authority"],
            ClientId: "routetimer-web",
            RedirectUri: "authentication/login-callback",
            PostLogoutRedirectUri: "/"));
    }
}
```

- [ ] **Step 5: Register the service and endpoints**

In `src/RouteTimer.Api/Program.cs`, alongside the other scoped registrations, add:

```csharp
builder.Services.AddScoped<ILocalCredentialRepository, LocalCredentialRepository>();
builder.Services.AddScoped<LocalCredentialService>();
```

Register `LocalCredentialService` in both modes. Minimal API parameter binding resolves handler
parameters from the container and fails at request time when a service is missing; making the
parameter nullable does not change that. The service is inert in Keycloak mode.

This requires `using RouteTimer.Services.Persistence;` and `using RouteTimer.Persistence.Repositories;`, both of which Program.cs already has for the other repositories — confirm before adding duplicates.

Then, alongside the other `Map*Endpoints()` calls after `var app = builder.Build();`:

```csharp
app.MapAuthEndpoints(authMode);
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~AuthConfigEndpointTests"`

Expected: PASS, 4 tests.

- [ ] **Step 7: Commit**

```bash
git add src/RouteTimer.Contracts/Auth src/RouteTimer.Api/Endpoints/AuthEndpoints.cs src/RouteTimer.Api/Program.cs tests/RouteTimer.Api.Tests/Auth/AuthConfigEndpointTests.cs
git commit -m "feat: serve authentication configuration at runtime"
```

---

## Task 5: Local setup, login and logout

**Files:**
- Create: `src/RouteTimer.Api/Auth/LocalAuthenticationDefaults.cs`
- Create: `tests/RouteTimer.Api.Tests/Auth/LocalAuthEndpointTests.cs`
- Modify: `src/RouteTimer.Api/Endpoints/AuthEndpoints.cs`
- Modify: `src/RouteTimer.Api/Program.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/RouteTimer.Api.Tests/Auth/LocalAuthEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Contracts.Auth;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Auth;

public sealed class LocalAuthEndpointTests
{
    private const string Passphrase = "correct horse battery staple";

    [Fact]
    public async Task Setup_configures_the_credential_and_signs_the_rider_in()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new SetLocalCredentialRequest(Passphrase),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers, header => header.Key == "Set-Cookie");

        using var profile = await client.GetAsync("/api/profile", CancellationToken.None);
        Assert.NotEqual(HttpStatusCode.Unauthorized, profile.StatusCode);
    }

    [Fact]
    public async Task Setup_is_refused_once_a_credential_exists()
    {
        await using var app = LocalApp("an-existing-hash");
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new SetLocalCredentialRequest(Passphrase),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Setup_rejects_a_passphrase_below_the_minimum_length()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new SetLocalCredentialRequest("short"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_the_configured_passphrase_grants_access_to_a_protected_endpoint()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);
        await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);

        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LocalLoginRequest(Passphrase),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await client.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session", CancellationToken.None);
        Assert.NotNull(session);
        Assert.True(session.Authenticated);
    }

    [Fact]
    public async Task Login_with_the_wrong_passphrase_is_rejected()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);
        await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);

        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LocalLoginRequest("not the passphrase at all"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);

        await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);

        var session = await client.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session", CancellationToken.None);
        Assert.NotNull(session);
        Assert.False(session.Authenticated);
    }

    [Fact]
    public async Task Protected_endpoints_reject_an_anonymous_caller_in_local_mode()
    {
        await using var app = LocalApp("an-existing-hash");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/profile", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static RouteTimerApiFactory LocalApp(string? initialHash) =>
        new RouteTimerApiFactory()
            .WithAuthMode("Local")
            .WithServices(services =>
            {
                services.RemoveAll<ILocalCredentialRepository>();
                services.AddSingleton<ILocalCredentialRepository>(
                    new AuthConfigEndpointTests.FakeLocalCredentialRepository(initialHash));
            });
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~LocalAuthEndpointTests"`

Expected: FAIL. Setup returns 404 because the endpoint does not exist yet.

- [ ] **Step 3: Add the scheme constants**

Create `src/RouteTimer.Api/Auth/LocalAuthenticationDefaults.cs`:

```csharp
namespace RouteTimer.Api.Auth;

public static class LocalAuthenticationDefaults
{
    public const string AuthenticationScheme = "RouteTimerLocal";

    public const string CookieName = "routetimer.session";

    /// <summary>
    /// The rider role the authorization policy requires. Local mode grants it to the single rider
    /// this deployment serves; Keycloak mode receives the same role from the realm.
    /// </summary>
    public const string RiderRole = "rider";
}
```

- [ ] **Step 4: Register the cookie scheme**

In `src/RouteTimer.Api/Program.cs`, add an `else` branch to the mode check added in Task 1:

```csharp
else
{
    builder.Services.AddAuthentication(LocalAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(LocalAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = LocalAuthenticationDefaults.CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // Local mode is expected to run over plain HTTP on loopback, where an
            // unconditionally Secure cookie would never be sent. SameAsRequest marks it
            // Secure whenever the request itself arrived over HTTPS.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            // This is an API, not a server-rendered site: answer with status codes rather
            // than redirecting to a login page that does not exist on the server.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
}
```

- [ ] **Step 5: Add the endpoints**

In `src/RouteTimer.Api/Endpoints/AuthEndpoints.cs`, add these usings at the top:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Errors;
```

Inside `MapAuthEndpoints`, before `return routes;`:

```csharp
        if (mode == AuthMode.Local)
        {
            routes.MapPost("/api/auth/setup", SetupAsync).AllowAnonymous();
            routes.MapPost("/api/auth/login", LoginAsync).AllowAnonymous();
            routes.MapPost("/api/auth/logout", LogoutAsync).AllowAnonymous();
        }
```

And add these methods to the class:

```csharp
    private static async Task<IResult> SetupAsync(
        SetLocalCredentialRequest request,
        LocalCredentialService credentials,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await credentials.SetupAsync(request.Passphrase, cancellationToken);
        switch (result)
        {
            case LocalCredentialSetupResult.AlreadyConfigured:
                return ApiProblems.Conflict(
                    ErrorCodes.LocalCredentialAlreadyConfigured,
                    "A passphrase has already been set for this installation. Sign in with it, or clear the stored credential to run first-use setup again.");
            case LocalCredentialSetupResult.TooShort:
                return ApiProblems.BadRequest(
                    ErrorCodes.LocalCredentialTooShort,
                    $"The passphrase must be at least {LocalCredentialService.MinimumPassphraseLength} characters.");
            default:
                await SignInAsync(context);
                return TypedResults.Ok(new AuthSessionResponse(true));
        }
    }

    private static async Task<IResult> LoginAsync(
        LocalLoginRequest request,
        LocalCredentialService credentials,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!await credentials.VerifyAsync(request.Passphrase, cancellationToken))
        {
            return ApiProblems.Create(
                StatusCodes.Status401Unauthorized,
                ErrorCodes.LocalCredentialRejected,
                "That passphrase was not recognised.");
        }

        await SignInAsync(context);
        return TypedResults.Ok(new AuthSessionResponse(true));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context)
    {
        await context.SignOutAsync(LocalAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.Ok(new AuthSessionResponse(false));
    }

    private static Task SignInAsync(HttpContext context)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "rider"),
                new Claim(ClaimTypes.Role, LocalAuthenticationDefaults.RiderRole)
            ],
            LocalAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return context.SignInAsync(
            LocalAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }
```

- [ ] **Step 6: Add the error codes**

In `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`, add these constants alongside the existing ones, matching the file's existing naming and formatting:

```csharp
    public const string LocalCredentialAlreadyConfigured = "local-credential-already-configured";
    public const string LocalCredentialTooShort = "local-credential-too-short";
    public const string LocalCredentialRejected = "local-credential-rejected";
    public const string LocalCredentialLockedOut = "local-credential-locked-out";
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~LocalAuthEndpointTests"`

Expected: PASS, 7 tests.

- [ ] **Step 8: Run the whole API suite**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false`

Expected: PASS, 0 failed.

- [ ] **Step 9: Commit**

```bash
git add src/RouteTimer.Api src/RouteTimer.Contracts/Errors/ErrorCodes.cs tests/RouteTimer.Api.Tests/Auth/LocalAuthEndpointTests.cs
git commit -m "feat: add local mode setup, login and logout"
```

---

## Task 6: Login rate limiting

**Files:**
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `src/RouteTimer.Api/Endpoints/AuthEndpoints.cs`
- Modify: `tests/RouteTimer.Api.Tests/Auth/LocalAuthEndpointTests.cs`

- [ ] **Step 1: Write the failing test**

Append to the `LocalAuthEndpointTests` class:

```csharp
    [Fact]
    public async Task Repeated_failed_logins_are_rate_limited()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);
        await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LocalLoginRequest("wrong passphrase entirely"),
                CancellationToken.None);
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~Repeated_failed_logins_are_rate_limited"`

Expected: FAIL with `Assert.Contains() Failure` — every attempt returns 401, none returns 429.

- [ ] **Step 3: Register the rate limiter**

In `src/RouteTimer.Api/Program.cs`, add to the usings:

```csharp
using System.Threading.RateLimiting;
```

Add before `var app = builder.Build();`:

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
```

This needs `using RouteTimer.Api.Endpoints;`, which Program.cs already has for the other endpoint modules.

Add after `var app = builder.Build();`, before the endpoint mappings:

```csharp
app.UseRateLimiter();
```

- [ ] **Step 4: Apply the policy to the login endpoint**

In `src/RouteTimer.Api/Endpoints/AuthEndpoints.cs`, add the policy name as a public constant at the top of the class:

```csharp
    public const string LoginRateLimitPolicy = "auth-login";
```

And change the login mapping to require it:

```csharp
            routes.MapPost("/api/auth/login", LoginAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~LocalAuthEndpointTests"`

Expected: PASS, 8 tests.

- [ ] **Step 6: Commit**

```bash
git add src/RouteTimer.Api tests/RouteTimer.Api.Tests/Auth/LocalAuthEndpointTests.cs
git commit -m "feat: rate limit local mode sign-in attempts"
```

---

## Task 7: Migration-state readiness

**Files:**
- Create: `src/RouteTimer.Api/Health/MigrationState.cs`
- Create: `src/RouteTimer.Api/Health/MigrationsReadyHealthCheck.cs`
- Create: `tests/RouteTimer.Api.Tests/MigrationsReadinessTests.cs`
- Modify: `src/RouteTimer.Api/DatabaseMigrationService.cs`
- Modify: `src/RouteTimer.Api/Program.cs`

`DatabaseMigrationService` is registered after the web host's own hosted service, so Kestrel begins listening before migrations run. The existing readiness check proves only that the database is reachable, so readiness can report healthy mid-migration. This task closes that gap.

- [ ] **Step 1: Write the failing test**

Create `tests/RouteTimer.Api.Tests/MigrationsReadinessTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Api.Health;
using RouteTimer.Persistence;

namespace RouteTimer.Api.Tests;

public sealed class MigrationsReadinessTests
{
    [Fact]
    public async Task Ready_is_unhealthy_while_migrations_are_still_pending()
    {
        await using var app = new MigrationReadinessApplicationFactory(migrationsRequired: true, completed: false);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/ready", CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Ready_is_healthy_once_migrations_have_completed()
    {
        await using var app = new MigrationReadinessApplicationFactory(migrationsRequired: true, completed: true);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/ready", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_is_healthy_when_this_deployment_does_not_apply_migrations()
    {
        await using var app = new MigrationReadinessApplicationFactory(migrationsRequired: false, completed: false);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/ready", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class MigrationReadinessApplicationFactory(bool migrationsRequired, bool completed)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(RouteTimer.Api.Auth.AuthModeResolver.ConfigurationKey, "Keycloak");
            builder.UseSetting("Database:ApplyMigrations", migrationsRequired ? "true" : "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<RouteTimerDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<RouteTimerDbContext>>();
                services.AddDbContext<RouteTimerDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();

                services.RemoveAll<MigrationState>();
                var state = new MigrationState(migrationsRequired);
                if (completed)
                {
                    state.MarkCompleted();
                }

                services.AddSingleton(state);
            });
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~MigrationsReadinessTests"`

Expected: FAIL to compile with `CS0246` for `MigrationState`.

- [ ] **Step 3: Add the state holder**

Create `src/RouteTimer.Api/Health/MigrationState.cs`:

```csharp
namespace RouteTimer.Api.Health;

/// <summary>
/// Tracks whether startup migrations have finished. The migration service is a hosted service and
/// starts after the web host's own, so Kestrel is already listening while migrations run. Without
/// this flag, readiness would report healthy against a database that is still migrating, and
/// Compose's --wait would return early.
/// </summary>
public sealed class MigrationState(bool migrationsRequired)
{
    private volatile bool completed;

    public bool IsReady => !migrationsRequired || completed;

    public void MarkCompleted() => completed = true;
}
```

- [ ] **Step 4: Add the health check**

Create `src/RouteTimer.Api/Health/MigrationsReadyHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RouteTimer.Api.Health;

public sealed class MigrationsReadyHealthCheck(MigrationState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsReady
            ? HealthCheckResult.Healthy("Database migrations are complete.")
            : HealthCheckResult.Unhealthy("Database migrations have not completed yet."));
}
```

- [ ] **Step 5: Set the flag when migrations finish**

In `src/RouteTimer.Api/DatabaseMigrationService.cs`, add the using:

```csharp
using RouteTimer.Api.Health;
```

Change the constructor to take the state:

```csharp
public sealed class DatabaseMigrationService(
    IServiceProvider services,
    ILogger<DatabaseMigrationService> logger,
    MigrationState migrationState) : IHostedService
```

In `StartAsync`, add `migrationState.MarkCompleted();` in two places. First, in the non-relational early return, because an in-memory provider has nothing to migrate:

```csharp
        if (!database.Database.IsRelational())
        {
            migrationState.MarkCompleted();
            return;
        }
```

Second, immediately after the successful migration log line:

```csharp
            await database.Database.MigrateAsync(cancellationToken);
            migrationState.MarkCompleted();
            logger.LogInformation("RouteTimer database migrations completed.");
```

Do not mark it complete in the `finally` block — a failed migration must leave readiness unhealthy.

- [ ] **Step 6: Register both in Program.cs**

In `src/RouteTimer.Api/Program.cs`, add the using:

```csharp
using RouteTimer.Api.Health;
```

Replace the existing health-check registration:

```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<RouteTimerDbContext>("database", tags: ["ready"]);
```

with:

```csharp
builder.Services.AddSingleton(new MigrationState(builder.Configuration.GetValue("Database:ApplyMigrations", false)));
builder.Services.AddHealthChecks()
    .AddDbContextCheck<RouteTimerDbContext>("database", tags: ["ready"])
    .AddCheck<MigrationsReadyHealthCheck>("migrations", tags: ["ready"]);
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~MigrationsReadinessTests"`

Expected: PASS, 3 tests.

- [ ] **Step 8: Run the whole API suite**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -p:UseSharedCompilation=false`

Expected: PASS, 0 failed. `Ready_health_is_anonymous_when_the_database_is_available` still passes because that factory leaves `Database:ApplyMigrations` unset, so no migrations are required.

- [ ] **Step 9: Commit**

```bash
git add src/RouteTimer.Api tests/RouteTimer.Api.Tests/MigrationsReadinessTests.cs
git commit -m "fix: hold readiness until migrations complete"
```

---

## Task 8: Client runtime authentication bootstrap

**Files:**
- Create: `src/RouteTimer.Client/Auth/ClientAuthConfig.cs`
- Create: `src/RouteTimer.Client/Auth/LocalAuthenticationStateProvider.cs`
- Create: `tests/RouteTimer.Client.Tests/Auth/LocalAuthenticationStateProviderTests.cs`
- Modify: `src/RouteTimer.Client/Program.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/RouteTimer.Client.Tests/Auth/LocalAuthenticationStateProviderTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using RouteTimer.Client.Auth;
using RouteTimer.Contracts.Auth;

namespace RouteTimer.Client.Tests.Auth;

public sealed class LocalAuthenticationStateProviderTests
{
    [Fact]
    public async Task Reports_an_anonymous_user_when_the_session_endpoint_says_so()
    {
        var provider = new LocalAuthenticationStateProvider(Client(new AuthSessionResponse(false)));

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Reports_an_authenticated_rider_when_the_session_endpoint_says_so()
    {
        var provider = new LocalAuthenticationStateProvider(Client(new AuthSessionResponse(true)));

        var state = await provider.GetAuthenticationStateAsync();

        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.True(state.User.IsInRole("rider"));
    }

    [Fact]
    public async Task Reports_an_anonymous_user_when_the_session_endpoint_is_unreachable()
    {
        var provider = new LocalAuthenticationStateProvider(FailingClient());

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Notifying_a_sign_in_refreshes_the_reported_state()
    {
        var handler = new SequenceHandler([new AuthSessionResponse(false), new AuthSessionResponse(true)]);
        var provider = new LocalAuthenticationStateProvider(
            new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var before = await provider.GetAuthenticationStateAsync();
        provider.NotifySessionChanged();
        var after = await provider.GetAuthenticationStateAsync();

        Assert.False(before.User.Identity?.IsAuthenticated);
        Assert.True(after.User.Identity?.IsAuthenticated);
    }

    private static HttpClient Client(AuthSessionResponse session) =>
        new(new SequenceHandler([session])) { BaseAddress = new Uri("https://localhost/") };

    private static HttpClient FailingClient() =>
        new(new FailingHandler()) { BaseAddress = new Uri("https://localhost/") };

    private sealed class SequenceHandler(IReadOnlyList<AuthSessionResponse> responses) : HttpMessageHandler
    {
        private int index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var session = responses[Math.Min(index, responses.Count - 1)];
            index++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(session)
            });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("unreachable");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~LocalAuthenticationStateProviderTests"`

Expected: FAIL to compile with `CS0246` for `LocalAuthenticationStateProvider`.

- [ ] **Step 3: Add the config shape**

Create `src/RouteTimer.Client/Auth/ClientAuthConfig.cs`:

```csharp
using RouteTimer.Contracts.Auth;

namespace RouteTimer.Client.Auth;

/// <summary>
/// The deployment's authentication configuration, fetched once before the host is built. Held as a
/// singleton so pages can ask which mode they are running in without another round trip.
/// </summary>
public sealed class ClientAuthConfig(AuthConfigResponse response)
{
    public const string LocalMode = "Local";

    public bool IsLocal => string.Equals(response.Mode, LocalMode, StringComparison.OrdinalIgnoreCase);

    public bool SetupRequired => response.SetupRequired;

    public AuthConfigResponse Response => response;
}
```

- [ ] **Step 4: Add the authentication state provider**

Create `src/RouteTimer.Client/Auth/LocalAuthenticationStateProvider.cs`:

```csharp
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using RouteTimer.Contracts.Auth;

namespace RouteTimer.Client.Auth;

/// <summary>
/// Reports local-mode authentication state by asking the API whether the session cookie the browser
/// is already sending is valid. The client never sees the cookie itself.
/// </summary>
public sealed class LocalAuthenticationStateProvider(HttpClient http) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var session = await http.GetFromJsonAsync<AuthSessionResponse>("api/auth/session");
            if (session?.Authenticated != true)
            {
                return Anonymous;
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "rider"),
                    new Claim(ClaimTypes.Role, "rider")
                ],
                authenticationType: "RouteTimerLocal",
                nameType: ClaimTypes.Name,
                roleType: ClaimTypes.Role);

            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch (HttpRequestException)
        {
            return Anonymous;
        }
    }

    /// <summary>Call after sign-in, first-run setup, or sign-out so the UI re-reads the session.</summary>
    public void NotifySessionChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~LocalAuthenticationStateProviderTests"`

Expected: PASS, 4 tests.

- [ ] **Step 6: Fetch the config before building the host**

Replace the whole of `src/RouteTimer.Client/Program.cs` with:

```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Auth;
using RouteTimer.Client.Jobs;
using RouteTimer.Client;
using RouteTimer.Contracts.Auth;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The deployment decides how it authenticates, so the client cannot know at build time. Fetch the
// configuration first; one published image then serves every deployment.
using var bootstrapClient = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
var authConfig = await bootstrapClient.GetFromJsonAsync<AuthConfigResponse>("api/auth/config")
    ?? throw new InvalidOperationException("The API did not return an authentication configuration.");
builder.Services.AddSingleton(new ClientAuthConfig(authConfig));

if (string.Equals(authConfig.Mode, ClientAuthConfig.LocalMode, StringComparison.OrdinalIgnoreCase))
{
    // The browser attaches the session cookie to same-origin requests on its own, so there is no
    // bearer handler in this mode.
    builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
    builder.Services.AddScoped<LocalAuthenticationStateProvider>();
    builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<LocalAuthenticationStateProvider>());
    builder.Services.AddAuthorizationCore();
}
else
{
    builder.Services.AddOidcAuthentication(options =>
    {
        options.ProviderOptions.Authority = authConfig.Authority;
        options.ProviderOptions.ClientId = authConfig.ClientId;
        options.ProviderOptions.RedirectUri = authConfig.RedirectUri;
        options.ProviderOptions.PostLogoutRedirectUri = authConfig.PostLogoutRedirectUri;
        options.ProviderOptions.ResponseType = "code";
    });
    builder.Services.AddScoped(sp =>
    {
        var handler = sp.GetRequiredService<AuthorizationMessageHandler>()
            .ConfigureHandler(authorizedUrls: [builder.HostEnvironment.BaseAddress]);
        return new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
    });
}

builder.Services.AddScoped<IRouteTimerApiClient>(sp => new RouteTimerApiClient(sp.GetRequiredService<HttpClient>()));
builder.Services.AddScoped<JobPoller>();
builder.Services.AddSingleton(TimeProvider.System);

await builder.Build().RunAsync();
```

- [ ] **Step 7: Remove the static Keycloak client settings**

Replace the whole of `src/RouteTimer.Client/wwwroot/appsettings.json` with:

```json
{
  "MapTiles": {
    "Url": "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
    "Attribution": "&copy; OpenStreetMap contributors"
  }
}
```

- [ ] **Step 8: Build the client**

Run: `dotnet build src/RouteTimer.Client/RouteTimer.Client.csproj -p:UseSharedCompilation=false`

Expected: `Build succeeded`, 0 warnings, 0 errors.

- [ ] **Step 9: Run the whole client suite**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj -p:UseSharedCompilation=false`

Expected: PASS, 0 failed. The bUnit tests construct components directly and never execute `Program.cs`, so they are unaffected by the bootstrap change.

- [ ] **Step 10: Commit**

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests/Auth/LocalAuthenticationStateProviderTests.cs
git commit -m "feat: select client authentication at runtime"
```

---

## Task 9: Local sign-in and first-run setup page

**Files:**
- Create: `src/RouteTimer.Client/Pages/LocalSignIn.razor`
- Create: `tests/RouteTimer.Client.Tests/Auth/LocalSignInPageTests.cs`
- Modify: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs` and `RouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/RedirectToLogin.razor`
- Modify: `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`

Before starting, open `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs` and `RouteTimerApiClient.cs` and match their existing method and error-handling style exactly.

- [ ] **Step 1: Add the client API methods**

In `IRouteTimerApiClient`, add:

```csharp
    Task<bool> SetupLocalCredentialAsync(string passphrase, CancellationToken ct);

    Task<bool> LocalLoginAsync(string passphrase, CancellationToken ct);
```

In `RouteTimerApiClient`, add implementations following the file's existing pattern for posting JSON and translating problem responses into `ApiProblemException`. Both return `true` on a 200 response. `SetupLocalCredentialAsync` posts `new SetLocalCredentialRequest(passphrase)` to `api/auth/setup`; `LocalLoginAsync` posts `new LocalLoginRequest(passphrase)` to `api/auth/login`. A 401 from login and a 409 or 400 from setup must surface as `ApiProblemException` so the page can show the API's own message.

In `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`, add matching hooks following the file's existing pattern:

```csharp
    public Func<string, CancellationToken, Task<bool>>? OnSetupLocalCredentialAsync { get; set; }
    public Func<string, CancellationToken, Task<bool>>? OnLocalLoginAsync { get; set; }

    public List<(string Passphrase, CancellationToken CancellationToken)> SetupLocalCredentials { get; } = [];
    public List<(string Passphrase, CancellationToken CancellationToken)> LocalLogins { get; } = [];

    public Task<bool> SetupLocalCredentialAsync(string passphrase, CancellationToken ct)
    {
        SetupLocalCredentials.Add((passphrase, ct));
        return OnSetupLocalCredentialAsync is not null
            ? OnSetupLocalCredentialAsync(passphrase, ct)
            : throw new NotSupportedException();
    }

    public Task<bool> LocalLoginAsync(string passphrase, CancellationToken ct)
    {
        LocalLogins.Add((passphrase, ct));
        return OnLocalLoginAsync is not null
            ? OnLocalLoginAsync(passphrase, ct)
            : throw new NotSupportedException();
    }
```

- [ ] **Step 2: Write the failing test**

Create `tests/RouteTimer.Client.Tests/Auth/LocalSignInPageTests.cs`:

```csharp
using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Auth;
using RouteTimer.Client.Pages;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Auth;

namespace RouteTimer.Client.Tests.Auth;

public sealed class LocalSignInPageTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();

    private void Arrange(bool setupRequired)
    {
        Services.AddSingleton<IRouteTimerApiClient>(api);
        Services.AddSingleton(new ClientAuthConfig(
            new AuthConfigResponse("Local", setupRequired, null, null, null, null)));
    }

    [Fact]
    public void First_run_shows_setup_wording_and_a_confirmation_field()
    {
        Arrange(setupRequired: true);

        var cut = Render<LocalSignIn>();

        Assert.Contains("Choose a passphrase", cut.Markup, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=local-signin-confirm]"));
    }

    [Fact]
    public void Returning_visit_shows_sign_in_wording_and_no_confirmation_field()
    {
        Arrange(setupRequired: false);

        var cut = Render<LocalSignIn>();

        Assert.Contains("Sign in", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=local-signin-confirm]"));
    }

    [Fact]
    public void Setup_refuses_to_submit_when_the_two_passphrases_differ()
    {
        Arrange(setupRequired: true);

        var cut = Render<LocalSignIn>();
        cut.Find("[data-testid=local-signin-passphrase]").Change("correct horse battery staple");
        cut.Find("[data-testid=local-signin-confirm]").Change("something else entirely");
        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("do not match", cut.Find("[data-testid=local-signin-error]").TextContent, StringComparison.Ordinal);
            Assert.Empty(api.SetupLocalCredentials);
        });
    }

    [Fact]
    public void Setup_submits_the_passphrase_when_both_fields_match()
    {
        Arrange(setupRequired: true);
        api.OnSetupLocalCredentialAsync = (_, _) => Task.FromResult(true);

        var cut = Render<LocalSignIn>();
        cut.Find("[data-testid=local-signin-passphrase]").Change("correct horse battery staple");
        cut.Find("[data-testid=local-signin-confirm]").Change("correct horse battery staple");
        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.SetupLocalCredentials);
            Assert.Equal("correct horse battery staple", api.SetupLocalCredentials[0].Passphrase);
        });
    }

    [Fact]
    public void Sign_in_submits_the_passphrase()
    {
        Arrange(setupRequired: false);
        api.OnLocalLoginAsync = (_, _) => Task.FromResult(true);

        var cut = Render<LocalSignIn>();
        cut.Find("[data-testid=local-signin-passphrase]").Change("correct horse battery staple");
        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() => Assert.Single(api.LocalLogins));
    }

    [Fact]
    public void A_rejected_passphrase_shows_the_api_problem_detail()
    {
        Arrange(setupRequired: false);
        api.OnLocalLoginAsync = (_, _) => Task.FromException<bool>(
            new ApiProblemException(
                HttpStatusCode.Unauthorized,
                "local-credential-rejected",
                "Sign-in failed",
                "That passphrase was not recognised."));

        var cut = Render<LocalSignIn>();
        cut.Find("[data-testid=local-signin-passphrase]").Change("wrong passphrase entirely");
        cut.Find("[data-testid=local-signin-submit]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains(
                "That passphrase was not recognised.",
                cut.Find("[data-testid=local-signin-error]").TextContent,
                StringComparison.Ordinal));
    }
}
```

`ApiProblemException`'s constructor is `(HttpStatusCode statusCode, string code, string title, string? detail, IReadOnlyDictionary<string, string[]>? errors = null)`. The page reads `Detail`, which is the fourth argument — putting the message in `title` and leaving `detail` null would show an empty error.

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~LocalSignInPageTests"`

Expected: FAIL to compile with `CS0246` for `LocalSignIn`.

- [ ] **Step 4: Write the page**

Create `src/RouteTimer.Client/Pages/LocalSignIn.razor`:

```razor
@page "/signin"
@layout RouteTimer.Client.Layout.MainLayout
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Authorization
@using RouteTimer.Client.Api
@using RouteTimer.Client.Auth
@inject IRouteTimerApiClient Api
@inject ClientAuthConfig AuthConfig
@inject NavigationManager Navigation
@inject IServiceProvider Services
@attribute [AllowAnonymous]

<PageTitle>@Title | RouteTimer</PageTitle>

<h1>@Title</h1>

@if (AuthConfig.SetupRequired)
{
    <p>
        This installation has no passphrase yet. Choose a passphrase to protect your training data.
        It is stored on this machine only, and there is no way to recover it — write it down somewhere safe.
    </p>
}
else
{
    <p>Enter the passphrase you chose when you first set up this installation.</p>
}

<form class="local-signin" @onsubmit="SubmitAsync" @onsubmit:preventDefault>
    <label for="local-signin-passphrase">Passphrase</label>
    <input id="local-signin-passphrase"
           data-testid="local-signin-passphrase"
           type="password"
           autocomplete="@(AuthConfig.SetupRequired ? "new-password" : "current-password")"
           value="@passphrase"
           @onchange="@(args => passphrase = args.Value?.ToString() ?? string.Empty)" />

    @if (AuthConfig.SetupRequired)
    {
        <label for="local-signin-confirm">Confirm passphrase</label>
        <input id="local-signin-confirm"
               data-testid="local-signin-confirm"
               type="password"
               autocomplete="new-password"
               value="@confirmation"
               @onchange="@(args => confirmation = args.Value?.ToString() ?? string.Empty)" />
    }

    @if (error is not null)
    {
        <p data-testid="local-signin-error" class="local-signin__error">@error</p>
    }

    <button data-testid="local-signin-submit" type="submit" disabled="@isSubmitting">
        @(isSubmitting ? "Working…" : Title)
    </button>
</form>

@code {
    private string passphrase = string.Empty;
    private string confirmation = string.Empty;
    private string? error;
    private bool isSubmitting;

    private string Title => AuthConfig.SetupRequired ? "Choose a passphrase" : "Sign in";

    private async Task SubmitAsync()
    {
        if (isSubmitting)
        {
            return;
        }

        error = null;

        if (AuthConfig.SetupRequired && !string.Equals(passphrase, confirmation, StringComparison.Ordinal))
        {
            error = "The two passphrases do not match.";
            return;
        }

        isSubmitting = true;
        try
        {
            if (AuthConfig.SetupRequired)
            {
                await Api.SetupLocalCredentialAsync(passphrase, CancellationToken.None);
            }
            else
            {
                await Api.LocalLoginAsync(passphrase, CancellationToken.None);
            }

            if (Services.GetService(typeof(LocalAuthenticationStateProvider)) is LocalAuthenticationStateProvider provider)
            {
                provider.NotifySessionChanged();
            }

            Navigation.NavigateTo("/", forceLoad: true);
        }
        catch (ApiProblemException problem)
        {
            error = problem.Detail;
        }
        catch (HttpRequestException)
        {
            error = "RouteTimer could not be reached. Check that the container is still running.";
        }
        finally
        {
            isSubmitting = false;
        }
    }
}
```

`src/RouteTimer.Client/_Imports.razor` imports `Microsoft.AspNetCore.Components.Authorization` but **not** `Microsoft.AspNetCore.Authorization`, so the `@using Microsoft.AspNetCore.Authorization` line shown above is required in this file for `[AllowAnonymous]` to resolve.

The page resolves `LocalAuthenticationStateProvider` through `IServiceProvider` rather than `@inject` because it is only registered in local mode, and the bUnit tests do not register it at all.

- [ ] **Step 5: Route unauthenticated local users to it**

Replace the whole of `src/RouteTimer.Client/RedirectToLogin.razor` with:

```razor
@using RouteTimer.Client.Auth
@inject NavigationManager Navigation
@inject ClientAuthConfig AuthConfig

@code {
    protected override void OnInitialized()
    {
        if (AuthConfig.IsLocal)
        {
            Navigation.NavigateTo("signin");
            return;
        }

        Navigation.NavigateTo($"authentication/login?returnUrl={Uri.EscapeDataString(Navigation.Uri)}");
    }
}
```

- [ ] **Step 6: Require authorization on the rider's pages**

`AuthorizeRouteView` only invokes its `NotAuthorized` fragment for pages carrying an `[Authorize]`
attribute. No page has one today, so `RedirectToLogin` has never run and the change in Step 5 would
have no effect on its own.

Add this line to each of the six pages below, directly beneath the existing `@page` directive:

```razor
@attribute [Microsoft.AspNetCore.Authorization.Authorize]
```

- `src/RouteTimer.Client/Pages/Home.razor`
- `src/RouteTimer.Client/Pages/Profile.razor`
- `src/RouteTimer.Client/Pages/Training.razor`
- `src/RouteTimer.Client/Pages/TrainingDetail.razor`
- `src/RouteTimer.Client/Pages/Predictions.razor`
- `src/RouteTimer.Client/Pages/PredictionDetail.razor`

Do **not** add it to `LocalSignIn.razor`, `Authentication.razor`, or `NotFound.razor`. The fully
qualified attribute name avoids adding a `@using` to six files.

The existing bUnit tests render these pages directly rather than through the router, so
`AuthorizeRouteView` is not involved and they are unaffected. If any test does fail with a message
about `AuthenticationStateProvider`, add `Services.AddAuthorizationCore()` and a test
`AuthenticationStateProvider` to that test's setup.

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj -p:UseSharedCompilation=false --filter "FullyQualifiedName~LocalSignInPageTests"`

Expected: PASS, 6 tests.

- [ ] **Step 8: Run the whole client suite**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj -p:UseSharedCompilation=false`

Expected: PASS, 0 failed. If any pre-existing test now fails resolving `ClientAuthConfig`, that test renders a component reaching `RedirectToLogin`; register a `ClientAuthConfig` in that test's service collection.

- [ ] **Step 9: Commit**

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat: add the local mode sign-in page"
```

---

## Task 10: Remove build-time authentication configuration

**Files:**
- Modify: `Dockerfile`
- Modify: `docker-compose.yml`
- Modify: `deploy/README.md`

- [ ] **Step 1: Strip the build arguments from the Dockerfile**

In `Dockerfile`, delete these four lines from the `build` stage:

```dockerfile
ARG KEYCLOAK_AUTHORITY
ARG ROUTETIMER_HOSTNAME
```

```dockerfile
RUN printf '{"Keycloak":{"authority":"%s","client_id":"routetimer-web","redirect_uri":"https://%s/authentication/login-callback","post_logout_redirect_uri":"https://%s/"}}' "$KEYCLOAK_AUTHORITY" "$ROUTETIMER_HOSTNAME" "$ROUTETIMER_HOSTNAME" > src/RouteTimer.Client/wwwroot/appsettings.Production.json
```

Leave every other line unchanged. The `HEALTHCHECK` instruction is added in Plan B, not here.

- [ ] **Step 2: Remove the build arguments from Compose and set the mode**

In `docker-compose.yml`, replace the `routetimer` service's `build` block:

```yaml
    build:
      context: .
      args:
        KEYCLOAK_AUTHORITY: ${KEYCLOAK_AUTHORITY:?set KEYCLOAK_AUTHORITY}
        ROUTETIMER_HOSTNAME: ${ROUTETIMER_HOSTNAME:?set ROUTETIMER_HOSTNAME}
```

with:

```yaml
    build:
      context: .
```

and add `Auth__Mode` to that service's `environment` block, above the existing `Keycloak__Authority` line:

```yaml
      Auth__Mode: Keycloak
```

`ROUTETIMER_HOSTNAME` is still used by `deploy/caddy/routetimer.caddy`, so leave any other reference to it alone.

- [ ] **Step 3: Update the deploy README**

In `deploy/README.md`, replace step 1 with:

```markdown
1. Set `ROUTETIMER_DB_PASSWORD`, `KEYCLOAK_AUTHORITY` (for example `https://auth.example.com/realms/routetimer`), and `ROUTETIMER_HOSTNAME` in the deployment environment. `Auth__Mode` is set to `Keycloak` by the Compose file and must not be removed: the application refuses to start without an explicit authentication mode. None of these is a build argument any more — the image is built once and configured at run time.
```

- [ ] **Step 4: Verify the image builds without the arguments**

Run:

```bash
docker build -t routetimer:auth-modes .
```

Expected: the build completes. It previously failed without `--build-arg KEYCLOAK_AUTHORITY=...`.

If Docker is not running, start Docker Desktop first. This is the only step in this plan that needs Docker.

- [ ] **Step 5: Run the full solution test suite**

Run: `dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false`

Expected: PASS across all suites, 0 failed. Persistence takes about 45 seconds and needs Docker for its PostgreSQL container.

- [ ] **Step 6: Verify a clean build**

Run: `dotnet build ./RouteTimer.slnx -p:UseSharedCompilation=false --no-incremental`

Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add Dockerfile docker-compose.yml deploy/README.md
git commit -m "refactor: configure authentication at run time rather than build time"
```

---

## Plan Complete

At this point one image runs in either mode, selected by `Auth__Mode`, and readiness waits for migrations. Plan B covers the Compose projects, run scripts, CI publishing, backup and restore scripts, runbook and deployment documentation, and public-repository preparation.
