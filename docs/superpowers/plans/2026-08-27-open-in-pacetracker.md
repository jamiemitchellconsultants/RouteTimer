# Plan: Open in PaceTracker Action on Prediction Detail Page

**Date:** 2026-08-27  
**Goal:** Add an "Open in PaceTracker" action button to the `PredictionDetail.razor` page that launches the RoutePacer Blazor PWA (`pacetracking.tqaentry.com`) and passes the prediction's timed GPX file using the cross-app invocation contract defined in the RoutePacer plan.

---

## Background

The RouteTimer Prediction Detail page (`/predictions/{Id}`) already surfaces:

- **Download GPX** — plain route geometry, `GET /api/predictions/{id}/gpx`
- **Download GPX with predicted times** — route + time tags, `GET /api/predictions/{id}/gpx?timed=true`
- **Send to Garmin** — pushes route to Garmin Connect

The RoutePacer Blazor PWA (`https://pacetracking.tqaentry.com`) can receive a GPX route via its cross-app invocation contract (Contract v1):

```
https://<routepacer-host>/open?src=rt&v=1&payload=<token-or-id>&name=<route-name>&ts=<unix-ms>&sig=<hmac>
```

RouteTimer must generate a short-lived signed URL pointing to the timed GPX bytes and embed it as the `payload` parameter.

---

## Architecture

```
PredictionDetail.razor
  └── "Open in PaceTracker" button
        └── calls: POST /api/predictions/{id}/pacetracker-link
              └── PredictionsEndpoints.cs handler
                    └── PaceTrackerLinkService.GenerateInvocationUrlAsync()
                          ├── Fetches timed GPX (reuses existing GetGpxSourceAsync)
                          ├── Stores GPX bytes in short-lived store (ITemporaryPayloadStore)
                          ├── Builds signed deep-link URL
                          └── Returns { url: string }
  └── Blazor JS interop: window.open(url, '_blank')
```

---

## Step-by-Step Implementation Plan

### Step 1 — API: Add `ITemporaryPayloadStore` abstraction and in-memory implementation

**File:** `src/RouteTimer.Services/PaceTracker/ITemporaryPayloadStore.cs` _(new)_

```csharp
public interface ITemporaryPayloadStore
{
    /// <summary>Stores GPX bytes and returns an opaque token valid for <paramref name="ttl"/>.</summary>
    Task<string> StoreAsync(byte[] content, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Retrieves GPX bytes by token. Returns null if expired or not found.</summary>
    Task<byte[]?> RetrieveAsync(string token, CancellationToken ct = default);
}
```

**File:** `src/RouteTimer.Services/PaceTracker/InMemoryTemporaryPayloadStore.cs` _(new)_

- Use `IMemoryCache` (already available in ASP.NET Core DI) keyed by a `Guid` token.
- `StoreAsync`: generate a `Guid` token, cache `(content, expiresAt)` with absolute expiry, return token string.
- `RetrieveAsync`: look up token, remove on first access (one-time retrieval) to prevent replay.
- Register as `ITemporaryPayloadStore` → `InMemoryTemporaryPayloadStore` (singleton or scoped — singleton is fine for in-memory).

> **Note:** For a production hardening pass, replace with a Redis-backed or database-backed store. The interface boundary makes this a drop-in swap.

---

### Step 2 — API: Add `PaceTrackerLinkService`

**File:** `src/RouteTimer.Services/PaceTracker/PaceTrackerLinkService.cs` _(new)_

Constructor dependencies:
- `IPredictionQueryService predictions`
- `ITemporaryPayloadStore payloadStore`
- `IOptions<PaceTrackerOptions> options`
- `TimeProvider timeProvider`

**Method:** `GenerateInvocationUrlAsync(Guid predictionId, CancellationToken ct)`

1. Call `predictions.GetGpxSourceAsync(predictionId, ct)` — reuse existing service.
2. Build timed GPX bytes:  
   `var gpxBytes = System.Text.Encoding.UTF8.GetBytes(PredictionGpxWriter.Write(source, timed: true));`
3. Store bytes via `ITemporaryPayloadStore.StoreAsync(gpxBytes, ttl: TimeSpan.FromMinutes(10), ct)` → get `token`.
4. Build invocation URL:
   ```
   {PaceTrackerOptions.BaseUrl}/open
     ?src=rt
     &v=1
     &payload={token}
     &name={Uri.EscapeDataString(source.RouteName)}
     &ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}
     &sig={ComputeHmac(token, ts, options.SigningKey)}
   ```
5. `ComputeHmac`: `HMACSHA256(key: signingKey, data: $"{token}:{ts}")` → base64url-encoded.
6. Return the full URL string.

---

### Step 3 — API: Add `PaceTrackerOptions` configuration class

**File:** `src/RouteTimer.Services/PaceTracker/PaceTrackerOptions.cs` _(new)_

```csharp
public class PaceTrackerOptions
{
    public const string SectionName = "PaceTracker";

    /// <summary>Base URL of the RoutePacer app, e.g. https://pacetracking.tqaentry.com</summary>
    public string BaseUrl { get; set; } = "https://pacetracking.tqaentry.com";

    /// <summary>HMAC signing key shared with RoutePacer for payload verification.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Payload TTL in minutes.</summary>
    public int PayloadTtlMinutes { get; set; } = 10;
}
```

Register in `appsettings.json` (and `appsettings.Development.json`):

```json
"PaceTracker": {
  "BaseUrl": "https://pacetracking.tqaentry.com",
  "SigningKey": "",
  "PayloadTtlMinutes": 10
}
```

> **Security note:** `SigningKey` must be a sufficiently random secret (≥ 32 bytes). In production, inject via environment variable / secret management; never commit a real key. For MVP the signature provides tamper-evidence; full trust boundary hardening is a follow-up.

Register in DI (e.g., in the API project's `Program.cs`):

```csharp
builder.Services.Configure<PaceTrackerOptions>(
    builder.Configuration.GetSection(PaceTrackerOptions.SectionName));
builder.Services.AddSingleton<ITemporaryPayloadStore, InMemoryTemporaryPayloadStore>();
builder.Services.AddScoped<PaceTrackerLinkService>();
```

---

### Step 4 — API: Add GPX payload retrieval endpoint

**File:** `src/RouteTimer.Api/Endpoints/PredictionsEndpoints.cs` — add two new endpoint mappings:

#### 4a. `POST /api/predictions/{id}/pacetracker-link`

- Resolves `PaceTrackerLinkService` from DI.
- Calls `GenerateInvocationUrlAsync(id, ct)`.
- Returns `200 OK` with `{ "url": "<full-invocation-url>" }`.
- Returns `404` if prediction not found; `422` if prediction has no segments/GPX.

#### 4b. `GET /api/pacetracker/payload/{token}`

- Resolves `ITemporaryPayloadStore` from DI.
- Calls `RetrieveAsync(token, ct)`.
- If `null`: returns `404` (expired or already consumed).
- If found: returns `200 OK` with `Content-Type: application/gpx+xml`, body = raw GPX bytes.
- This endpoint is publicly accessible (no auth required) — it is reachable by the RoutePacer app after the signed deep link is opened; the token itself is the credential. The one-time-retrieval pattern prevents repeated access.

> **Note on payload delivery model:**  
> The RoutePacer plan describes three payload modes. This implementation uses **mode 1** (short-lived signed URL to GPX bytes): RouteTimer generates a token, stores the GPX temporarily, and embeds a retrieval URL as the `payload` query parameter. RoutePacer fetches that URL to get the GPX bytes.

---

### Step 5 — Client: Add `IRouteTimerApiClient.CreatePaceTrackerLinkAsync` method

**File:** `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs` — add:

```csharp
Task<PaceTrackerLinkResponse> CreatePaceTrackerLinkAsync(Guid predictionId, CancellationToken ct = default);
```

**File:** `src/RouteTimer.Client/Api/RouteTimerApiClient.cs` — implement:

```csharp
public async Task<PaceTrackerLinkResponse> CreatePaceTrackerLinkAsync(Guid predictionId, CancellationToken ct)
{
    var response = await _http.PostAsync($"api/predictions/{predictionId}/pacetracker-link", null, ct);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadFromJsonAsync<PaceTrackerLinkResponse>(ct)
        ?? throw new InvalidOperationException("Empty response from pacetracker-link endpoint.");
}
```

**File:** `src/RouteTimer.Contracts/Predictions/PaceTrackerLinkResponse.cs` _(new)_

```csharp
public record PaceTrackerLinkResponse(string Url);
```

---

### Step 6 — Client: Add JS interop for `window.open`

> Blazor WASM cannot directly call `window.open`. A small JS shim is needed.

**File:** `src/RouteTimer.Client/wwwroot/interop.js` _(new, or append to existing interop file if one exists)_

```js
window.routeTimerInterop = {
    openUrl: function (url) {
        window.open(url, '_blank', 'noopener,noreferrer');
    }
};
```

Reference in `index.html` (or `App.razor` host page):

```html
<script src="interop.js"></script>
```

**File:** `src/RouteTimer.Client/Interop/WindowInterop.cs` _(new)_

```csharp
public class WindowInterop(IJSRuntime js)
{
    public ValueTask OpenUrlAsync(string url) =>
        js.InvokeVoidAsync("routeTimerInterop.openUrl", url);
}
```

Register in DI (scoped):

```csharp
builder.Services.AddScoped<WindowInterop>();
```

---

### Step 7 — Client: Add "Open in PaceTracker" button to `PredictionDetail.razor`

**File:** `src/RouteTimer.Client/Pages/PredictionDetail.razor`

#### 7a. Inject new dependencies

```razor
@inject WindowInterop Window
```

(The `Api` injection already exists.)

#### 7b. Add UI in the `prediction-detail-actions` block (inside the `orderedSegments.Count > 0` guard)

Place after the existing GPX download buttons and before the Garmin section:

```razor
<div class="prediction-detail-actions prediction-detail-actions--pacetracker">
    <button type="button"
            data-testid="prediction-open-pacetracker"
            class="predictions-button predictions-button--primary"
            @onclick="OpenInPaceTrackerAsync"
            disabled="@openingPaceTracker">
        @(openingPaceTracker ? "Opening…" : "Open in PaceTracker")
    </button>

    @if (paceTrackerProblem is not null || paceTrackerFallbackProblem)
    {
        <div data-testid="prediction-pacetracker-error">
            <ProblemMessage Problem="@paceTrackerProblem"
                            FallbackMessage="We could not open PaceTracker. Please try again." />
        </div>
    }
</div>
```

#### 7c. Add state fields in `@code`

```csharp
private bool openingPaceTracker;
private ApiProblemException? paceTrackerProblem;
private bool paceTrackerFallbackProblem;
```

#### 7d. Add handler method

```csharp
private async Task OpenInPaceTrackerAsync()
{
    if (openingPaceTracker || prediction is null)
        return;

    openingPaceTracker = true;
    paceTrackerProblem = null;
    paceTrackerFallbackProblem = false;

    try
    {
        var link = await Api.CreatePaceTrackerLinkAsync(Id, pageCancellation.Token);
        await Window.OpenUrlAsync(link.Url);
    }
    catch (ApiProblemException apiProblem)
    {
        paceTrackerProblem = apiProblem;
    }
    catch (HttpRequestException)
    {
        paceTrackerFallbackProblem = true;
    }
    catch (OperationCanceledException) when (pageCancellation.IsCancellationRequested)
    {
        return;
    }
    finally
    {
        if (!pageCancellation.IsCancellationRequested)
            openingPaceTracker = false;
    }
}
```

---

### Step 8 — Tests

#### Unit tests

**File:** `tests/RouteTimer.Services.Tests/PaceTracker/PaceTrackerLinkServiceTests.cs` _(new)_

Cover:
- Happy path: valid prediction → returns URL with correct query parameters (`src=rt`, `v=1`, `name`, `ts`, `sig`, `payload`).
- GPX bytes are stored in the temporary store with the correct TTL.
- `sig` is a non-empty base64url string derived from the token and timestamp.
- Prediction not found propagates `NotFoundException` (or whatever the service convention is).

**File:** `tests/RouteTimer.Services.Tests/PaceTracker/InMemoryTemporaryPayloadStoreTests.cs` _(new)_

Cover:
- Store and retrieve round-trip returns correct bytes.
- One-time retrieval: second call returns `null`.
- Expired entry returns `null`.

#### Integration / endpoint tests

**File:** `tests/RouteTimer.Api.Tests/Endpoints/PaceTrackerEndpointTests.cs` _(new)_

Cover:
- `POST /api/predictions/{id}/pacetracker-link` returns `200` with a `url` field for a valid completed prediction.
- `POST /api/predictions/{id}/pacetracker-link` returns `404` for unknown prediction id.
- `GET /api/pacetracker/payload/{token}` returns GPX bytes on first call.
- `GET /api/pacetracker/payload/{token}` returns `404` on second call (one-time).
- `GET /api/pacetracker/payload/{expired-token}` returns `404`.

#### Blazor component tests (bUnit or Playwright)

**File:** `tests/RouteTimer.Client.Tests/Pages/PredictionDetailTests.cs` _(extend existing)_

Cover:
- "Open in PaceTracker" button is rendered when `orderedSegments.Count > 0`.
- Clicking button calls `Api.CreatePaceTrackerLinkAsync` and then `WindowInterop.OpenUrlAsync` with the returned URL.
- Button shows "Opening…" state while the async call is in flight.
- API error renders the ProblemMessage block with `data-testid="prediction-pacetracker-error"`.
- Button is not rendered when `orderedSegments.Count == 0`.

---

## File Summary

| Action | File |
|--------|------|
| New | `src/RouteTimer.Services/PaceTracker/ITemporaryPayloadStore.cs` |
| New | `src/RouteTimer.Services/PaceTracker/InMemoryTemporaryPayloadStore.cs` |
| New | `src/RouteTimer.Services/PaceTracker/PaceTrackerLinkService.cs` |
| New | `src/RouteTimer.Services/PaceTracker/PaceTrackerOptions.cs` |
| New | `src/RouteTimer.Contracts/Predictions/PaceTrackerLinkResponse.cs` |
| Modify | `src/RouteTimer.Api/Endpoints/PredictionsEndpoints.cs` |
| Modify | `src/RouteTimer.Api/Program.cs` (DI registrations) |
| Modify | `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs` |
| Modify | `src/RouteTimer.Client/Api/RouteTimerApiClient.cs` |
| New | `src/RouteTimer.Client/Interop/WindowInterop.cs` |
| New | `src/RouteTimer.Client/wwwroot/interop.js` |
| Modify | `src/RouteTimer.Client/wwwroot/index.html` (add script tag) |
| Modify | `src/RouteTimer.Client/Pages/PredictionDetail.razor` |
| Modify | `appsettings.json` / `appsettings.Development.json` |
| New | `tests/RouteTimer.Services.Tests/PaceTracker/PaceTrackerLinkServiceTests.cs` |
| New | `tests/RouteTimer.Services.Tests/PaceTracker/InMemoryTemporaryPayloadStoreTests.cs` |
| New | `tests/RouteTimer.Api.Tests/Endpoints/PaceTrackerEndpointTests.cs` |
| Modify | `tests/RouteTimer.Client.Tests/Pages/PredictionDetailTests.cs` |

---

## Constraints and Notes

- **Button visibility guard:** Only show "Open in PaceTracker" when `orderedSegments.Count > 0` — same condition as the existing GPX download buttons. A prediction without segments has no GPX to send.
- **Timed GPX only:** Always pass `timed: true` to `PredictionGpxWriter.Write` so RoutePacer receives time tags for pace computation.
- **No new API surface on RoutePacer side in this plan:** This plan covers only the RouteTimer side. RoutePacer must already implement (or later implement) its invocation contract intake per the RoutePacer plan.
- **Signing key in MVP:** The `SigningKey` may be left empty for the initial MVP; the HMAC field will be present but trivially derived. Full security hardening (key rotation, TTL enforcement on RoutePacer side) is tracked in the RoutePacer plan.
- **`window.open` and popup blockers:** The JS interop call must be triggered directly from the button click handler (not deferred into an awaited continuation) to avoid browser popup blocker. The current design calls `OpenUrlAsync` *after* awaiting the API call, which may be blocked. If this is a problem, the preferred workaround is to open the RoutePacer URL immediately (with a loading page or placeholder) and then pass the token via `postMessage` or redirect — log this as a follow-up if popup blocking is observed in testing.
- **One-time retrieval:** The payload token is consumed on first retrieval to prevent replay. RoutePacer must fetch the payload on first open; refreshing the RoutePacer page after token consumption will fail to re-fetch but the route will already be in IndexedDB.
