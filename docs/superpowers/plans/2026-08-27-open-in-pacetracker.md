# Open in PaceTracker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a rider send a completed prediction from a private, locally hosted RouteTimer to PaceTracker on a phone by uploading its timed GPX to the public RoutePacer relay and displaying a signed, expiring QR deep link.

**Architecture:** RouteTimer's authenticated API generates the existing timed GPX, uploads it over outbound HTTPS to the same-origin RoutePacer relay, validates the returned single-use payload URL, and signs RoutePacer Contract v1 with ECDSA P-256. The Blazor client shows the signed `/open` URL as a locally generated QR code with copy, same-device, expiry, recreation, and manual-download recovery; RouteTimer exposes no public payload endpoint.

**Tech Stack:** .NET 10, C# 14, ASP.NET Core minimal APIs and typed `HttpClient`, Blazor WebAssembly, ECDSA P-256/SHA-256, Node.js tests, `qrcode` 1.5.4 bundled by `esbuild` 0.25.9, xUnit 2.9.3, bUnit 2.9.0, and existing Docker deployment artifacts.

**Spec:** `docs/superpowers/specs/2026-08-27-open-in-pacetracker-design.md`

## Global Constraints

- RouteTimer remains private: add no anonymous payload endpoint, public hostname, inbound port, CORS policy, or LAN-address handoff.
- RouteTimer uploads only through outbound HTTPS to the exact configured RoutePacer origin.
- RoutePacer relay contract is `POST /api/handoffs` and `GET /api/handoffs/{43-character-base64url-token}` with a fixed ten-minute lifetime.
- Relay content is plaintext by explicit decision; RouteTimer must not imply end-to-end encryption.
- Contract v1 query keys are `src`, `v`, `payload`, `name`, `ts`, and `sig`, each exactly once and emitted in that order.
- Contract v1 signs UTF-8 `rt\n1\n{payload-absolute-uri}\n{name-or-empty}\n{unix-milliseconds}` with no trailing line feed.
- Signatures use ECDSA P-256/SHA-256, IEEE-P1363 fixed-width bytes, and unpadded base64url.
- The relay upload credential and signing private key stay server-side and out of source, responses, logs, exception details, browser assets, and Docker image layers.
- Timed GPX is always produced with `PredictionGpxWriter.Write(source, timed: true)`; the existing download and Garmin paths remain unchanged.
- The action is visible only for predictions with stored segments and when server-side handoff configuration is enabled.
- QR generation is entirely local and calls no external QR service.
- RoutePacer repository work is not implemented here; use `docs/superpowers/prompts/2026-08-27-routepacer-public-handoff-relay.md` in that repository.
- `Narrative.md` is generated and never hand-edited. A decision-bearing PR requires the `narrative-required` label and the exact Narrative Context, Decision, and Consequences body headings.

---

## File and Responsibility Map

| Area | Files | Responsibility |
|---|---|---|
| Contract and crypto | `src/RouteTimer.Services/RoutePacer/RoutePacerContract.cs`, `IRoutePacerInvocationSigner.cs`, `EcdsaRoutePacerInvocationSigner.cs` | Canonical bytes, base64url P1363 signing, and deterministic invocation URL construction. |
| Relay boundary | `src/RouteTimer.Services/RoutePacer/IRoutePacerRelayClient.cs`, `src/RouteTimer.Api/RoutePacer/RoutePacerRelayClient.cs` | Upload exact GPX bytes and validate the public relay grant without following redirects. |
| Handoff workflow | `src/RouteTimer.Services/RoutePacer/RoutePacerHandoffService.cs` | Read prediction, write timed GPX, upload, sign, and return URL plus expiry. |
| Configuration | `src/RouteTimer.Api/RoutePacer/RoutePacerHandoffOptions.cs`, `RoutePacerHandoffOptionsValidator.cs`, `src/RouteTimer.Api/Program.cs` | Fail-closed feature flag, origin, upload credential, private key, typed client, and redaction. |
| API | `src/RouteTimer.Contracts/Predictions/RoutePacerContracts.cs`, `src/RouteTimer.Api/Endpoints/RoutePacerEndpoints.cs`, `ErrorCodes.cs` | Authenticated status and handoff endpoints with stable public errors. |
| Client API | `IRouteTimerApiClient.cs`, `RouteTimerApiClient.cs`, `FakeRouteTimerApiClient.cs` | Typed status and handoff calls with test observability. |
| QR UI | `src/RouteTimer.Client/Components/PaceTrackerHandoff.razor*`, `RoutePacer/PaceTrackerQrInterop.cs`, `wwwroot/js/pace-tracker-qr*.mjs` | Origin validation, local QR rendering, expiry, copy, recreation, and disposal. |
| Prediction page | `src/RouteTimer.Client/Pages/PredictionDetail.razor*` | Feature discovery, create action, handoff panel, errors, and manual fallback. |
| Assets and deployment | `package*.json`, `scripts/build-vendor.mjs`, `appsettings*.json`, `deploy/*.yml`, `README.md`, `RUNBOOK.md` | Reproducible QR bundle, disabled defaults, secret injection, rollout, and recovery. |
| Tests | `tests/RouteTimer.Services.Tests/RoutePacer`, `tests/RouteTimer.Api.Tests/RoutePacer`, existing API/client test files | Contract fixtures, relay behavior, error mapping, authorization, UI states, and secret/log safety. |

### Task 1: Freeze Contract v1 Canonicalization and ECDSA Signing

**Files:**
- Create: `src/RouteTimer.Services/RoutePacer/RoutePacerContract.cs`
- Create: `src/RouteTimer.Services/RoutePacer/IRoutePacerInvocationSigner.cs`
- Create: `src/RouteTimer.Services/RoutePacer/EcdsaRoutePacerInvocationSigner.cs`
- Create: `src/RouteTimer.Services/RoutePacer/DisabledRoutePacerInvocationSigner.cs`
- Create: `tests/RouteTimer.Services.Tests/RoutePacer/Fixtures/routepacer-contract-v1.json`
- Create: `tests/RouteTimer.Services.Tests/RoutePacer/RoutePacerContractTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj`

**Interfaces:**
- Consumes: absolute RoutePacer base URI, absolute relay payload URI, route name, and issued-at instant.
- Produces: `RoutePacerContract.CanonicalBytes(Uri, string?, long)`, `RoutePacerContract.BuildInvocationUrl(Uri, Uri, string?, DateTimeOffset, IRoutePacerInvocationSigner)`, and `IRoutePacerInvocationSigner.Sign(ReadOnlySpan<byte>)`.

- [ ] **Step 1: Add the fixed cross-repository fixture**

Create `routepacer-contract-v1.json` with the exact test-only values below and copy it to the test output directory from the test project:

```json
{
  "version": 1,
  "publicJwk": {
    "kty": "EC",
    "x": "eF97z6UgPFxtHzeoAzuf_FIPvlmyQXtTXljf80NHLr0",
    "y": "g9KVk0X5YDaiuLOO88a1QyAtZ5n9wQwbcDkW7enM6oQ",
    "crv": "P-256"
  },
  "privateKeyPem": "-----BEGIN PRIVATE KEY-----\nMIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg2Ylwv8R3sYAMK3mj\n/BhpxW9UXtZtVEfTJdiHpk26dOWhRANCAAR4X3vPpSA8XG0fN6gDO5/8Ug++WbJB\ne1NeWN/zQ0cuvYPSlZNF+WA2orizjvPGtUMgLWeZ/cEMG3A5Fu3pzOqE\n-----END PRIVATE KEY-----\n",
  "payloadUrl": "https://pacetracking.tqaentry.com/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
  "name": "Kingston & Dorking",
  "issuedUnixMilliseconds": 1787832000000,
  "canonical": "rt\n1\nhttps://pacetracking.tqaentry.com/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\nKingston & Dorking\n1787832000000",
  "signature": "T57agZOYCwa3hUGbXWUICfn87S2izTwwcOUXHh5YHtzsj5Zlhzi0UOZmHQqo3AIZ7QtV133Pv8idmJjl81YrwQ",
  "invocationUrl": "https://pacetracking.tqaentry.com/open?src=rt&v=1&payload=https%3A%2F%2Fpacetracking.tqaentry.com%2Fapi%2Fhandoffs%2FAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&name=Kingston%20%26%20Dorking&ts=1787832000000&sig=T57agZOYCwa3hUGbXWUICfn87S2izTwwcOUXHh5YHtzsj5Zlhzi0UOZmHQqo3AIZ7QtV133Pv8idmJjl81YrwQ"
}
```

Add this item to `RouteTimer.Services.Tests.csproj`:

```xml
<None Update="RoutePacer/Fixtures/routepacer-contract-v1.json" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 2: Write failing fixture, encoding, and signature tests**

Add tests named:

```csharp
[Fact]
public void Canonical_bytes_match_the_shared_contract_fixture();

[Fact]
public void Invocation_url_matches_the_shared_contract_fixture();

[Theory]
[InlineData(null)]
[InlineData("")]
[InlineData("Café & coast / return")]
public void Name_is_signed_unescaped_and_query_encoded_once(string? name);

[Fact]
public void Signer_returns_64_byte_P1363_signature_as_base64url();

[Fact]
public void Signer_rejects_a_non_P256_private_key();
```

The first two tests deserialize the checked-in fixture, import its test private key, and compare exact canonical text, signature, and URL. Decode the returned signature and assert 64 bytes, no `=`, `+`, or `/`.

- [ ] **Step 3: Run the focused tests and observe the missing types**

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RoutePacerContractTests -m:1 /nodeReuse:false -tl:off
```

Expected: FAIL because `RoutePacerContract` and the signer types do not exist.

- [ ] **Step 4: Implement the minimal contract and signer**

Implement these exact signatures:

```csharp
public interface IRoutePacerInvocationSigner
{
    string Sign(ReadOnlySpan<byte> canonicalBytes);
}

public sealed class EcdsaRoutePacerInvocationSigner : IRoutePacerInvocationSigner, IDisposable
{
    public static EcdsaRoutePacerInvocationSigner FromPem(string privateKeyPem);
    public string Sign(ReadOnlySpan<byte> canonicalBytes);
    public void Dispose();
}

public sealed class DisabledRoutePacerInvocationSigner : IRoutePacerInvocationSigner
{
    public string Sign(ReadOnlySpan<byte> canonicalBytes) =>
        throw new InvalidOperationException("RoutePacer handoff signing is disabled.");
}

public static class RoutePacerContract
{
    public const string Source = "rt";
    public const int Version = 1;

    public static byte[] CanonicalBytes(Uri payloadUrl, string? name, long issuedUnixMilliseconds);

    public static Uri BuildInvocationUrl(
        Uri routePacerBaseUrl,
        Uri payloadUrl,
        string? name,
        DateTimeOffset issuedAt,
        IRoutePacerInvocationSigner signer);
}
```

Use `ECDsa.ImportFromPem`, require `ExportParameters(false).Curve.Oid.Value` to equal `ECCurve.NamedCurves.nistP256.Oid.Value`, and call:

```csharp
ecdsa.SignData(
    canonicalBytes,
    HashAlgorithmName.SHA256,
    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
```

Encode with `Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')`. Build the six query pairs explicitly in contract order with `Uri.EscapeDataString`; do not use form encoding where spaces become `+`.

- [ ] **Step 5: Run contract tests**

Run the Step 3 command again.

Expected: PASS with the exact fixed fixture.

- [ ] **Step 6: Commit the contract boundary**

```bash
git add src/RouteTimer.Services/RoutePacer tests/RouteTimer.Services.Tests/RoutePacer tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj
git commit -m "feat: freeze RoutePacer invocation contract"
```

### Task 2: Add Validated Configuration and the Public Relay Client

**Files:**
- Create: `src/RouteTimer.Services/RoutePacer/IRoutePacerRelayClient.cs`
- Create: `src/RouteTimer.Services/RoutePacer/RoutePacerRelayExceptions.cs`
- Create: `src/RouteTimer.Api/RoutePacer/RoutePacerHandoffOptions.cs`
- Create: `src/RouteTimer.Api/RoutePacer/RoutePacerHandoffOptionsValidator.cs`
- Create: `src/RouteTimer.Api/RoutePacer/RoutePacerRelayClient.cs`
- Create: `tests/RouteTimer.Api.Tests/RoutePacer/RoutePacerHandoffOptionsTests.cs`
- Create: `tests/RouteTimer.Api.Tests/RoutePacer/RoutePacerRelayClientTests.cs`

**Interfaces:**
- Consumes: `RoutePacerHandoffOptions`, raw timed GPX bytes, `TimeProvider`, and the frozen relay HTTP contract.
- Produces: `IRoutePacerRelayClient.UploadAsync(byte[], CancellationToken)` returning `RoutePacerRelayGrant(Uri PayloadUrl, DateTimeOffset ExpiresAt)` or a typed `RoutePacerRelayException` with `RoutePacerRelayFailure`.

- [ ] **Step 1: Write failing option-validation tests**

Cover disabled empty secrets as success, and enabled configurations failing separately for empty upload key, empty/invalid PEM, HTTP base URL, base URL path/query/fragment, and a non-P256 key. A valid enabled fixture uses `https://pacetracking.tqaentry.com` and the Task 1 test key.

Run:

```bash
dotnet test tests/RouteTimer.Api.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RoutePacerHandoffOptionsTests -m:1 /nodeReuse:false -tl:off
```

Expected: FAIL because the options and validator do not exist.

- [ ] **Step 2: Implement the options and validator**

Use:

```csharp
public sealed class RoutePacerHandoffOptions
{
    public const string SectionName = "RoutePacerHandoff";
    public bool Enabled { get; init; }
    public string RoutePacerBaseUrl { get; init; } = "https://pacetracking.tqaentry.com";
    public string RelayUploadKey { get; init; } = string.Empty;
    public string SigningPrivateKeyPem { get; init; } = string.Empty;
}
```

Implement `IValidateOptions<RoutePacerHandoffOptions>`. Always validate that the base URL is an HTTPS origin; only require and import-check secrets when `Enabled` is true. Dispose the temporary ECDSA object immediately after validation.

- [ ] **Step 3: Write failing relay request and response-validation tests**

Use a recording `HttpMessageHandler` and fixed `TimeProvider`. Test:

- exact `POST /api/handoffs`, `application/gpx+xml`, `Cache-Control: no-store`, bearer credential, and byte-identical body;
- `HttpCompletionOption.ResponseHeadersRead` behavior through a content stream that records first read;
- valid `201` response;
- no redirect following (the handler returns `302`, which must be treated as invalid);
- rejection of HTTP/foreign-origin payload URL, wrong path, token length/alphabet, query, fragment, already-expired grant, and expiry more than ten minutes plus thirty seconds ahead;
- typed mappings for `401`, `413`, `415`, `429` with valid `Retry-After`, `5xx`, timeout, and malformed JSON.

Run:

```bash
dotnet test tests/RouteTimer.Api.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RoutePacerRelayClientTests -m:1 /nodeReuse:false -tl:off
```

Expected: FAIL because the relay client boundary does not exist.

- [ ] **Step 4: Implement relay types and the typed client**

Use:

```csharp
public sealed record RoutePacerRelayGrant(Uri PayloadUrl, DateTimeOffset ExpiresAt);

public interface IRoutePacerRelayClient
{
    Task<RoutePacerRelayGrant> UploadAsync(byte[] timedGpx, CancellationToken cancellationToken);
}

public enum RoutePacerRelayFailure
{
    Authentication,
    PayloadTooLarge,
    RejectedPayload,
    RateLimited,
    Unavailable,
    InvalidResponse
}

public sealed class RoutePacerRelayException(
    RoutePacerRelayFailure failure,
    string message,
    TimeSpan? retryAfter = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public RoutePacerRelayFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
```

The production client constructor takes `HttpClient`, `RoutePacerHandoffOptions`, and `TimeProvider`. Never include response bodies, request URLs, route names, or credentials in exception messages. Enforce the exact grant validation rules from the spec.

- [ ] **Step 5: Register the hardened typed client in `Program.cs`**

Bind with `ValidateOnStart`, register `TimeProvider` before consumers, and configure:

```csharp
builder.Services.AddHttpClient<IRoutePacerRelayClient, RoutePacerRelayClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<RoutePacerHandoffOptions>>().Value;
    client.BaseAddress = new Uri(options.RoutePacerBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false
})
.RedactLoggedHeaders(["Authorization"]);
```

Set `Authorization` per request inside the client, not as a mutable default header.

- [ ] **Step 6: Run both focused suites and commit**

```bash
dotnet test tests/RouteTimer.Api.Tests --no-restore -p:UseSharedCompilation=false --filter "FullyQualifiedName~RoutePacerHandoffOptionsTests|FullyQualifiedName~RoutePacerRelayClientTests" -m:1 /nodeReuse:false -tl:off
git add src/RouteTimer.Services/RoutePacer src/RouteTimer.Api/RoutePacer src/RouteTimer.Api/Program.cs tests/RouteTimer.Api.Tests/RoutePacer
git commit -m "feat: add RoutePacer relay client"
```

Expected: PASS; the committed sources contain no real credential.

### Task 3: Orchestrate Timed GPX Upload and Signed Handoff Creation

**Files:**
- Create: `src/RouteTimer.Services/RoutePacer/RoutePacerHandoffService.cs`
- Create: `tests/RouteTimer.Services.Tests/RoutePacer/RoutePacerHandoffServiceTests.cs`

**Interfaces:**
- Consumes: `PredictionQueryService`, `IRoutePacerRelayClient`, `IRoutePacerInvocationSigner`, RoutePacer base URI, feature enabled flag, and `TimeProvider`.
- Produces: `CreateAsync(Guid, CancellationToken)` returning `RoutePacerHandoff(Uri Url, DateTimeOffset ExpiresAt)` and typed disabled/missing exceptions; `PredictionNotCompleteException` remains the existing incomplete signal.

- [ ] **Step 1: Write failing workflow tests**

Use fakes for the prediction repository, relay, and signer. Add tests proving:

```csharp
[Fact]
public async Task Create_uploads_the_exact_timed_GPX_and_signs_the_validated_grant();

[Fact]
public async Task Disabled_handoff_does_not_read_the_prediction_or_call_the_relay();

[Fact]
public async Task Missing_prediction_does_not_call_the_relay();

[Fact]
public async Task Segment_free_prediction_does_not_call_the_relay();

[Fact]
public async Task Relay_failure_does_not_attempt_to_sign();
```

Assert that `<time>` appears inside `<trkseg>`, the exact route name is signed, `TimeProvider.GetUtcNow()` supplies `ts`, and the relay's expiry is returned unchanged.

- [ ] **Step 2: Run and observe failure**

```bash
dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RoutePacerHandoffServiceTests -m:1 /nodeReuse:false -tl:off
```

Expected: FAIL because `RoutePacerHandoffService` does not exist.

- [ ] **Step 3: Implement the workflow types**

Use these public types:

```csharp
public sealed record RoutePacerHandoffConfiguration(bool Enabled, Uri RoutePacerBaseUrl);
public sealed record RoutePacerHandoff(Uri Url, DateTimeOffset ExpiresAt);
public sealed class RoutePacerHandoffDisabledException() : Exception("The RoutePacer handoff is disabled.");
public sealed class RoutePacerPredictionMissingException() : Exception("The prediction was not found.");

public sealed class RoutePacerHandoffService(
    PredictionQueryService predictions,
    IRoutePacerRelayClient relay,
    IRoutePacerInvocationSigner signer,
    RoutePacerHandoffConfiguration configuration,
    TimeProvider timeProvider)
{
    public Task<RoutePacerHandoff> CreateAsync(Guid predictionId, CancellationToken cancellationToken);
}
```

Check `Enabled` before any other work. Resolve the source, UTF-8 encode `PredictionGpxWriter.Write(source, timed: true)`, upload it, then call `RoutePacerContract.BuildInvocationUrl` with `timeProvider.GetUtcNow()`.

- [ ] **Step 4: Run the workflow and existing GPX tests**

```bash
dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter "FullyQualifiedName~RoutePacerHandoffServiceTests|FullyQualifiedName~PredictionGpxWriterTests" -m:1 /nodeReuse:false -tl:off
```

Expected: PASS; existing timed and untimed GPX behavior remains unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/RouteTimer.Services/RoutePacer/RoutePacerHandoffService.cs tests/RouteTimer.Services.Tests/RoutePacer/RoutePacerHandoffServiceTests.cs
git commit -m "feat: create signed RoutePacer handoffs"
```

### Task 4: Expose Authenticated Status and Handoff Endpoints

**Files:**
- Create: `src/RouteTimer.Contracts/Predictions/RoutePacerContracts.cs`
- Create: `src/RouteTimer.Api/Endpoints/RoutePacerEndpoints.cs`
- Modify: `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `tests/RouteTimer.Api.Tests/RouteTimerApiFactory.cs`
- Create: `tests/RouteTimer.Api.Tests/Endpoints/RoutePacerEndpointTests.cs`

**Interfaces:**
- Consumes: validated options and `RoutePacerHandoffService`.
- Produces: `GET /api/routepacer/status` returning `RoutePacerStatusResponse`; `POST /api/predictions/{id}/routepacer-handoff` returning `RoutePacerHandoffResponse` or stable problem details.

- [ ] **Step 1: Write failing authorization, success, and mapping tests**

Add tests for unauthenticated `401`, authenticated non-rider `403`, disabled status, enabled status, successful creation, missing `404`, incomplete `409`, and every relay mapping. Assert the POST is rejected by the existing same-origin middleware when `Sec-Fetch-Site: cross-site` is present. Assert there is no anonymous `/api/routepacer/payloads/*` endpoint.

Use fixed contracts:

```csharp
public sealed record RoutePacerStatusResponse(bool Enabled, string RoutePacerOrigin);
public sealed record RoutePacerHandoffResponse(string Url, DateTimeOffset ExpiresAt);
```

- [ ] **Step 2: Run endpoint tests and observe 404/missing types**

```bash
dotnet test tests/RouteTimer.Api.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RoutePacerEndpointTests -m:1 /nodeReuse:false -tl:off
```

Expected: FAIL because the endpoints and contracts do not exist.

- [ ] **Step 3: Add stable error codes and endpoint mapping**

Add constants:

```csharp
public const string RoutePacerHandoffDisabled = "routepacer-handoff-disabled";
public const string RoutePacerRelayAuthenticationFailed = "routepacer-relay-authentication-failed";
public const string RoutePacerPayloadTooLarge = "routepacer-payload-too-large";
public const string RoutePacerRelayRejectedPayload = "routepacer-relay-rejected-payload";
public const string RoutePacerRelayRateLimited = "routepacer-relay-rate-limited";
public const string RoutePacerRelayUnavailable = "routepacer-relay-unavailable";
```

`MapRoutePacerEndpoints` maps both endpoints without `.AllowAnonymous()`. Status reads `IOptions<RoutePacerHandoffOptions>` and returns the base URI origin even while disabled. The POST maps service exceptions to the exact statuses in the spec and copies only a validated integer-seconds `Retry-After` header for rate limiting.

- [ ] **Step 4: Complete DI without importing disabled secrets**

Register `RoutePacerHandoffConfiguration` from validated options. Register the signer with a disabled implementation when `Enabled` is false and `EcdsaRoutePacerInvocationSigner.FromPem` when true. The disabled signer throws if called; Task 3 proves it is never called. Register the service scoped and call `app.MapRoutePacerEndpoints()` beside the other API mappings.

Extend `RouteTimerApiFactory.DefaultSettings` with disabled RoutePacer defaults. Enabled endpoint tests override the options and replace `IRoutePacerRelayClient` with a fake so they never use the network or a real secret.

- [ ] **Step 5: Run endpoint and security regression tests**

```bash
dotnet test tests/RouteTimer.Api.Tests --no-restore -p:UseSharedCompilation=false --filter "FullyQualifiedName~RoutePacerEndpointTests|FullyQualifiedName~SameOriginEnforcementTests|FullyQualifiedName~PredictionEndpointTests" -m:1 /nodeReuse:false -tl:off
```

Expected: PASS; prediction downloads remain authenticated and unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/RouteTimer.Contracts src/RouteTimer.Api/Endpoints/RoutePacerEndpoints.cs src/RouteTimer.Api/Program.cs tests/RouteTimer.Api.Tests
git commit -m "feat: expose RoutePacer handoff API"
```

### Task 5: Extend the Typed Blazor API Boundary

**Files:**
- Modify: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`
- Modify: `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`
- Modify: `tests/RouteTimer.Client.Tests/Api/RouteTimerApiClientTests.cs`

**Interfaces:**
- Consumes: Task 4 response contracts and routes.
- Produces: `GetRoutePacerStatusAsync(CancellationToken)` and `CreateRoutePacerHandoffAsync(Guid, CancellationToken)` across the real and fake clients.

- [ ] **Step 1: Write failing client request tests**

Add tests asserting exact methods and paths:

```csharp
await client.GetRoutePacerStatusAsync(ct); // GET /api/routepacer/status
await client.CreateRoutePacerHandoffAsync(id, ct); // POST /api/predictions/{id}/routepacer-handoff, empty body
```

Also assert both operations use the common `EnsureSuccessAsync` path so problem details become `ApiProblemException` instead of `HttpRequestException`.

- [ ] **Step 2: Run the focused tests**

```bash
dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RouteTimerApiClientTests -m:1 /nodeReuse:false -tl:off
```

Expected: FAIL at compile time because the interface methods are absent.

- [ ] **Step 3: Implement the two operations and fake observability**

Add:

```csharp
Task<RoutePacerStatusResponse> GetRoutePacerStatusAsync(CancellationToken ct);
Task<RoutePacerHandoffResponse> CreateRoutePacerHandoffAsync(Guid predictionId, CancellationToken ct);
```

Implement them through existing `GetRequiredAsync<T>` and `SendAsync<T>` helpers. In the fake add delegates, call lists, and default disabled status:

```csharp
public Func<CancellationToken, Task<RoutePacerStatusResponse>>? OnGetRoutePacerStatusAsync { get; set; }
public Func<Guid, CancellationToken, Task<RoutePacerHandoffResponse>>? OnCreateRoutePacerHandoffAsync { get; set; }
public List<CancellationToken> RequestedRoutePacerStatuses { get; } = [];
public List<(Guid PredictionId, CancellationToken CancellationToken)> CreatedRoutePacerHandoffs { get; } = [];
```

- [ ] **Step 4: Run all client API tests and commit**

```bash
dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RouteTimerApiClientTests -m:1 /nodeReuse:false -tl:off
git add src/RouteTimer.Client/Api tests/RouteTimer.Client.Tests/Api tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs
git commit -m "feat: add RoutePacer client operations"
```

Expected: PASS.

### Task 6: Build the Local QR Handoff Component

**Files:**
- Modify: `src/RouteTimer.Client/package.json`
- Modify: `src/RouteTimer.Client/package-lock.json`
- Modify: `src/RouteTimer.Client/scripts/build-vendor.mjs`
- Create: `src/RouteTimer.Client/scripts/qrcode-entry.mjs`
- Create: `src/RouteTimer.Client/wwwroot/js/pace-tracker-qr-core.mjs`
- Create: `src/RouteTimer.Client/wwwroot/js/pace-tracker-qr.mjs`
- Create: `src/RouteTimer.Client/wwwroot/js/pace-tracker-qr.test.mjs`
- Create: `src/RouteTimer.Client/RoutePacer/PaceTrackerQrInterop.cs`
- Create: `src/RouteTimer.Client/Components/PaceTrackerHandoff.razor`
- Create: `src/RouteTimer.Client/Components/PaceTrackerHandoff.razor.css`
- Create: `tests/RouteTimer.Client.Tests/Components/PaceTrackerHandoffTests.cs`
- Modify: `src/RouteTimer.Client/Program.cs`

**Interfaces:**
- Consumes: validated `RoutePacerHandoffResponse`, independent expected origin, `BrowserInterop`, `TimeProvider`, and recreate/close callbacks.
- Produces: local SVG QR rendering, expiry state, copy link, ordinary same-device anchor, recreation, close, and deterministic disposal.

- [ ] **Step 1: Pin and bundle QR dependencies**

Run from `src/RouteTimer.Client`:

```bash
npm install --save-exact qrcode@1.5.4
npm install --save-dev --save-exact esbuild@0.25.9
```

Create `scripts/qrcode-entry.mjs` that imports `qrcode` and exports only `toString`. Extend `build-vendor.mjs` to call esbuild with `bundle: true`, `platform: "browser"`, `format: "esm"`, and output `wwwroot/vendor/qrcode/qrcode.mjs`. Do not add a CDN or global script tag.

- [ ] **Step 2: Write failing pure JavaScript tests**

Export from `pace-tracker-qr-core.mjs`:

```javascript
export function validateHandoffUrl(url, expectedOrigin, nowMilliseconds, expiresAtMilliseconds)
```

Return the normalized URL only when it is HTTPS, has the exact expected origin, uses `/open`, and has not expired. Throw stable messages for invalid URL, insecure URL, foreign origin, wrong path, and expiry. Test each case plus Unicode/query preservation.

Run:

```bash
cd src/RouteTimer.Client && npm test
```

Expected: FAIL because the QR core module and tests are new and the function is absent.

- [ ] **Step 3: Implement the QR modules and interop**

`pace-tracker-qr.mjs` imports the vendored `toString`, calls `validateHandoffUrl`, renders SVG with error correction `M`, margin `2`, width `256`, and replaces the target element's children. Export `render(element, url, expectedOrigin, now, expiresAt)` and `clear(element)`.

Implement:

```csharp
public sealed class PaceTrackerQrInterop(IJSRuntime js) : IAsyncDisposable
{
    public Task RenderAsync(
        ElementReference element,
        string url,
        string expectedOrigin,
        DateTimeOffset now,
        DateTimeOffset expiresAt);

    public Task ClearAsync(ElementReference element);
    public ValueTask DisposeAsync();
}
```

Use dynamic import `./js/pace-tracker-qr.mjs` and cache/dispose the module like `BrowserInterop`.

- [ ] **Step 4: Write failing bUnit component tests**

Cover:

- instruction, expiry, manual timed-GPX fallback, and stable test IDs;
- one JS `render` call with the link, independently supplied origin, now, and expiry;
- copy calls `BrowserInterop.CopyToClipboardAsync` with the exact link;
- `Open on this device` is an HTTPS `_blank` anchor with `rel="noopener noreferrer"`;
- expired handoff disables copy/navigation and offers `Create a new code`;
- recreate and close callbacks fire once;
- rerender clears old QR and disposal disposes the module.

Use a fixed `FakeTimeProvider`; do not use wall-clock sleeps.

- [ ] **Step 5: Implement `PaceTrackerHandoff` and register interop**

Parameters are:

```csharp
[Parameter, EditorRequired] public RoutePacerHandoffResponse Handoff { get; set; } = default!;
[Parameter, EditorRequired] public string RoutePacerOrigin { get; set; } = string.Empty;
[Parameter, EditorRequired] public string TimedGpxDownloadUrl { get; set; } = string.Empty;
[Parameter] public EventCallback OnRecreate { get; set; }
[Parameter] public EventCallback OnClose { get; set; }
```

Render QR, exact phone instruction, UTC/local formatted expiry through existing formatting helpers, action buttons, and the fallback link. Register `PaceTrackerQrInterop` scoped. Reuse the already registered `BrowserInterop` and `TimeProvider`.

Start one cancellable expiry transition with `Task.Delay(expiresAt - timeProvider.GetUtcNow(), timeProvider, cancellationToken)`, then use `InvokeAsync(StateHasChanged)`. Cancel and dispose the component-owned token source on replacement and disposal. This makes an already displayed code become expired without wall-clock sleeps or polling.

- [ ] **Step 6: Run JavaScript and component tests**

```bash
cd src/RouteTimer.Client && npm run build:vendor && npm test
cd ../.. && dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~PaceTrackerHandoffTests -m:1 /nodeReuse:false -tl:off
```

Expected: PASS; `rg -n "https?://.*qr|api.qr" src/RouteTimer.Client` finds no external QR service.

- [ ] **Step 7: Commit**

```bash
git add src/RouteTimer.Client/package.json src/RouteTimer.Client/package-lock.json src/RouteTimer.Client/scripts src/RouteTimer.Client/wwwroot/js src/RouteTimer.Client/wwwroot/vendor/qrcode src/RouteTimer.Client/RoutePacer src/RouteTimer.Client/Components/PaceTrackerHandoff* src/RouteTimer.Client/Program.cs tests/RouteTimer.Client.Tests/Components
git commit -m "feat: render PaceTracker handoff QR locally"
```

### Task 7: Integrate Handoff State into Prediction Detail

**Files:**
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor.css`
- Modify: `tests/RouteTimer.Client.Tests/PredictionDetailPageTests.cs`

**Interfaces:**
- Consumes: Task 5 API calls and Task 6 `PaceTrackerHandoff` component.
- Produces: feature discovery, visible action for eligible predictions, creating/error/success/recreate/close states, and cancellation on disposal.

- [ ] **Step 1: Add failing page-state tests**

Extend the existing test class with:

```csharp
[Fact] public void Hides_PaceTracker_action_when_server_reports_disabled();
[Fact] public void Shows_action_only_for_segment_backed_prediction_when_enabled();
[Fact] public void Click_creates_handoff_once_and_renders_the_QR_component();
[Fact] public void Shows_creating_state_and_suppresses_duplicate_clicks();
[Fact] public void Maps_API_problem_to_the_PaceTracker_problem_block();
[Fact] public void Recreate_requests_a_fresh_handoff_and_replaces_the_old_one();
[Fact] public void Close_removes_the_handoff_without_deleting_the_relay_payload();
[Fact] public void Disposing_page_cancels_status_and_creation_requests();
```

Configure the fake's status and creation delegates explicitly. Assert stable test IDs `prediction-open-pacetracker`, `prediction-pacetracker-creating`, `prediction-pacetracker-error`, and `prediction-pacetracker-handoff`.

- [ ] **Step 2: Run and observe missing UI**

```bash
dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~PredictionDetailPageTests -m:1 /nodeReuse:false -tl:off
```

Expected: FAIL because no PaceTracker UI or calls exist.

- [ ] **Step 3: Load status fail-closed after eligible prediction load**

After `GetPredictionAsync` succeeds, call `GetRoutePacerStatusAsync` only when `orderedSegments.Count > 0`. Store enabled/origin only after a successful response whose origin is absolute HTTPS. On an API/network failure, hide the integration and leave prediction, download, visualization, and Garmin states untouched.

- [ ] **Step 4: Implement create, recreate, close, and error state**

Add one guarded method `CreateRoutePacerHandoffAsync`. Clear the prior handoff/error before creation, call the API with `pageCancellation.Token`, store the response, and render `PaceTrackerHandoff` with `$"/api/predictions/{Id}/gpx?timed=true"`. Map `ApiProblemException` through `ProblemMessage`, network failure to a fixed fallback, and page cancellation to no update. Recreate calls the same guarded method; close only clears local UI state.

Place the action after the GPX download links and before Garmin. Keep it inside the existing `orderedSegments.Count > 0` guard. Use button text `Creating code…` during the request and `Open in PaceTracker` otherwise.

- [ ] **Step 5: Run page and neighboring regression tests**

```bash
dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter "FullyQualifiedName~PredictionDetailPageTests|FullyQualifiedName~PredictionsPageTests|FullyQualifiedName~PaceTrackerHandoffTests" -m:1 /nodeReuse:false -tl:off
```

Expected: PASS; Garmin and GPX controls remain present.

- [ ] **Step 6: Commit**

```bash
git add src/RouteTimer.Client/Pages/PredictionDetail.razor src/RouteTimer.Client/Pages/PredictionDetail.razor.css tests/RouteTimer.Client.Tests/PredictionDetailPageTests.cs
git commit -m "feat: add PaceTracker QR handoff to predictions"
```

### Task 8: Add Deployment Configuration, Safety Checks, and Operational Handoff

**Files:**
- Modify: `src/RouteTimer.Api/appsettings.json`
- Modify: `src/RouteTimer.Api/appsettings.Development.json`
- Modify: `deploy/docker-compose.yml`
- Modify: `deploy/docker-compose.local.yml`
- Modify: `deploy/README.md`
- Modify: `README.md`
- Modify: `RUNBOOK.md`
- Create: `docs/routepacer-handoff.md`
- Create: `tests/RouteTimer.Api.Tests/RoutePacer/RoutePacerSecretSafetyTests.cs`
- Modify: `tests/RouteTimer.EndToEnd.Tests/GarminDeploymentTests.cs` or create `tests/RouteTimer.EndToEnd.Tests/RoutePacerDeploymentTests.cs`

**Interfaces:**
- Consumes: all production configuration keys and the separately deployed RoutePacer relay.
- Produces: disabled tracked defaults, environment-only secrets, no inbound exposure, rollout/rollback instructions, fixture verification, and static safety assertions.

- [ ] **Step 1: Write failing configuration and deployment safety tests**

Assert:

- tracked appsettings set `Enabled` false and contain empty secret values;
- Compose passes `RoutePacerHandoff__Enabled`, `RoutePacerHandoff__RelayUploadKey`, and `RoutePacerHandoff__SigningPrivateKeyPem` from environment/secret inputs;
- no Compose `ports` entry or Caddy route is added for the handoff;
- no `RouteTimerPublicBaseUrl`, HMAC setting, or `/api/routepacer/payloads` route remains;
- source and published `wwwroot` contain neither `BEGIN PRIVATE KEY` nor `RelayUploadKey` values;
- logging redacts `Authorization` for the relay client.

Run:

```bash
dotnet test tests/RouteTimer.Api.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RoutePacerSecretSafetyTests -m:1 /nodeReuse:false -tl:off
dotnet test tests/RouteTimer.EndToEnd.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RoutePacerDeploymentTests -m:1 /nodeReuse:false -tl:off
```

Expected: FAIL until configuration and docs are updated.

- [ ] **Step 2: Add disabled defaults and secret injection**

Tracked API configuration is:

```json
"RoutePacerHandoff": {
  "Enabled": false,
  "RoutePacerBaseUrl": "https://pacetracking.tqaentry.com",
  "RelayUploadKey": "",
  "SigningPrivateKeyPem": ""
}
```

Compose passes the upload key as `${ROUTEPACER_RELAY_UPLOAD_KEY:-}` and the PEM as `${ROUTEPACER_SIGNING_PRIVATE_KEY_PEM:-}`; enabled startup validation makes empty expansion fail closed. Document that the operator keeps both values in an untracked, permission-restricted `.env` file and uses Docker Compose's multiline single-quoted value syntax for the complete PKCS#8 PEM, including its real BEGIN/END lines. Do not place an example private key or realistic upload credential in tracked deploy files.

- [ ] **Step 3: Write the operator and user documentation**

`docs/routepacer-handoff.md` must include topology, relay contract link, plaintext ten-minute privacy consequence, key generation/public-JWK export, upload credential provisioning, exact deployment order, real-phone QR smoke test, first/second fetch check, expiry check, aggregate-only monitoring, and disable-first rollback. README explains the feature and manual timed-GPX fallback. RUNBOOK adds diagnosis for disabled action, `401`, `429`, relay outage, expired QR, clock skew, and public-key mismatch without asking operators to print secrets or URLs.

Reference `docs/superpowers/prompts/2026-08-27-routepacer-public-handoff-relay.md` as the RoutePacer-side planning input.

- [ ] **Step 4: Run the safety tests and full verification**

```bash
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off
cd src/RouteTimer.Client && npm run build:vendor && npm test
cd ../.. && dotnet build RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off
git diff --check
rg -n "HMAC|RouteTimerPublicBaseUrl|/api/routepacer/payloads|SigningKey" src tests deploy docs/routepacer-handoff.md
```

Expected: all tests and builds PASS; `git diff --check` is silent; the final `rg` is silent except historical superseded documentation outside the implementation scope, which must be identified explicitly rather than ignored.

- [ ] **Step 5: Commit deployment and documentation**

```bash
git add src/RouteTimer.Api/appsettings*.json deploy README.md RUNBOOK.md docs/routepacer-handoff.md tests/RouteTimer.Api.Tests/RoutePacer tests/RouteTimer.EndToEnd.Tests
git commit -m "docs: deploy PaceTracker relay handoff safely"
```

### Task 9: Cross-Repository Readiness Gate

**Files:**
- Verify: `docs/superpowers/prompts/2026-08-27-routepacer-public-handoff-relay.md`
- Verify: `docs/superpowers/specs/2026-08-27-open-in-pacetracker-design.md`
- Verify: `docs/superpowers/plans/2026-08-27-open-in-pacetracker.md`
- Verify: `tests/RouteTimer.Services.Tests/RoutePacer/Fixtures/routepacer-contract-v1.json`

**Interfaces:**
- Consumes: completed RouteTimer implementation and the RoutePacer implementation produced from the companion prompt.
- Produces: a go/no-go result for enabling either side; this task changes no application code.

- [ ] **Step 1: Compare the frozen contracts**

In both repositories, verify exact equality of fixture `version`, public JWK, payload URL, name, timestamp, canonical text, signature, and invocation URL. Verify RoutePacer accepts the valid fixture and rejects a fixture with each signed field mutated.

- [ ] **Step 2: Run a production-like private-to-public flow**

Start RouteTimer without a public ingress, start the public-origin relay and RoutePacer intake in a production-like environment, enable only test credentials, create a completed prediction handoff, decode/scan the displayed QR in a phone-sized browser context, import the route, and assert the relay returns `404` on a second fetch.

- [ ] **Step 3: Verify expiry, logging, and rollback**

Advance or wait beyond ten minutes in a controlled test environment, assert the payload is unavailable, and inspect application/ingress logs for absence of upload key, token, payload URL, invocation query, signature, route name, and GPX. Disable RouteTimer first and assert the action disappears without affecting GPX download; then disable RoutePacer intake.

- [ ] **Step 4: Record the go/no-go decision**

Do not enable production unless Steps 1–3 pass. Record only aggregate status, tested commit identifiers, origins, and timestamps—never the fixture private key, production secrets, live tokens, signed URLs, route names, or GPX content.

---

## Completion Checklist

- Every task was executed with its required TDD red/green cycle and commit.
- RouteTimer has no public payload endpoint and no new inbound network exposure.
- The relay upload credential and ECDSA private key exist only in server-side secret configuration.
- Contract fixture, canonicalization, and P1363 signature match RoutePacer byte-for-byte.
- The QR opens the public RoutePacer origin on a phone and the relay payload succeeds once.
- Plaintext ten-minute relay processing is disclosed and operationally bounded.
- Manual timed-GPX download remains usable when the relay or phone handoff is unavailable.
- Full .NET, Node, build, deployment-safety, and production-like readiness checks pass.
