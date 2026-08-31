[← Plan overview](README.md)

# Forecast-Adjusted Download UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in checkbox and reliable browser download flow for the forecast-adjusted timed GPX.

**Architecture:** The API client fetches the file and exposes bytes plus safe response metadata. `BrowserInterop` streams bytes into a JS Blob and always revokes its object URL. Prediction detail conditionally uses this path only when weather is selected.

**Tech Stack:** Blazor WebAssembly, HttpClient, JS modules, bUnit, Node test runner.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Raw GPX remains an ordinary anchor.
- With checkbox clear, timed GPX remains the existing anchor and makes no API-client/JS call.
- Checkbox is enabled only when `SupportsWeatherAdjustedDownload` is true.
- Garmin and pacing-adjustment actions never read or change this checkbox.
- Dispose/cancel cleanly when navigating away.

### Task 11: Add fetch/blob download and Prediction detail controls

**Files:**

- Create: `src/RouteTimer.Client/Api/ClientFileDownload.cs`
- Modify: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/RouteBuilder/BrowserInterop.cs`
- Modify: `src/RouteTimer.Client/wwwroot/js/browser.js`
- Create: `src/RouteTimer.Client/wwwroot/js/browser.test.mjs`
- Modify: `src/RouteTimer.Client/package.json`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor.css`
- Modify: `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`
- Modify: `tests/RouteTimer.Client.Tests/Api/RouteTimerApiClientTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionDetailPageTests.cs`

**Interfaces:**

```csharp
public sealed record ClientFileDownload(string FileName, string ContentType, byte[] Content);
Task<ClientFileDownload> DownloadWeatherAdjustedGpxAsync(Guid predictionId, CancellationToken ct);
public Task DownloadAsync(ClientFileDownload file);
```

- [ ] **Step 1: Write failing API-client download tests**

Assert GET path `/api/predictions/{id}/gpx?timed=true&weather=current`, exact bytes/content type, RFC content-disposition filename extraction, safe fallback `route-weather-adjusted.gpx`, Problem Details mapping, and cancellation. Reject path separators/control characters in server filename by falling back.

- [ ] **Step 2: Implement `DownloadWeatherAdjustedGpxAsync`**

Use a dedicated `HttpRequestMessage`, existing `EnsureSuccessAsync`, bounded byte reading with 50 MB upper bound, and `ContentDispositionHeaderValue.FileNameStar` before `FileName`. Strip quotes and apply `Path.GetFileName`; accept only a non-empty `.gpx` leaf.

- [ ] **Step 3: Write failing JS module tests**

Implement/export this exact behavior and test it with fake `URL`, `Blob`, `document`, anchor, and stream reference:

```javascript
export async function downloadFile(fileName, contentType, streamReference) {
  const bytes = await streamReference.arrayBuffer();
  const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
  try {
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
  } finally {
    URL.revokeObjectURL(url);
  }
}
```

Assert click values and revoke on both success and click failure. Change package test script to `node --test wwwroot/js/*.test.mjs` so existing tests remain included.

- [ ] **Step 4: Implement BrowserInterop streaming**

Create a read-only `MemoryStream`, wrap it in `DotNetStreamReference`, await module `downloadFile`, then dispose after JS reads the stream. Validate filename/content type/content length first.

- [ ] **Step 5: Write failing Prediction detail UI tests**

Assert supported checkbox starts clear; unsupported legacy guidance; unchecked original timed href; checked weather button; busy state; exact prediction ID; JS invocation; success cleanup; Problem Details; HTTP fallback; cancellation; and unchanged raw GPX/Garmin/adjustment behavior.

- [ ] **Step 6: Implement UI state and disposal**

Add `adjustForCurrentWeather`, `downloadingWeather`, problem/fallback fields, and linked cancellation. Keep existing timed anchor when unchecked. When checked, render `prediction-download-weather-timed`, call API then BrowserInterop, and show inline `prediction-weather-download-error`. Dispose only the download source without cancelling unrelated page loads.

- [ ] **Step 7: Run JS, API-client, and page tests**

```bash
npm test --prefix src/RouteTimer.Client
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~RouteTimerApiClientTests|FullyQualifiedName~PredictionDetailPageTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: all pass.

- [ ] **Step 8: Commit and push**

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat: add forecast-adjusted timed GPX download"
git push
git status --short
```

Expected: successful push and empty status.
