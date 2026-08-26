# Predictions Route Builder, GPX Export, and Garmin Course Push Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the rider start a prediction from a Google Maps URL as well as a GPX upload, save their Google Maps API key encrypted at rest, download any completed prediction as GPX, and push it to Garmin Connect as a course.

**Architecture:** The Google Maps capability is a port of the MapToGarmin Blazor WebAssembly app (`~/RiderProjects/MapToGarmin`) into `RouteTimer.Client`; it builds the GPX in the browser and submits it through the prediction upload endpoint that already exists, so there is no second submission path or job type. Short-link expansion moves from MapToGarmin's Caddy shim into a RouteTimer API route. The API key is stored in one encrypted row using a generalised version of the AES-GCM protector that already guards Garmin tokens. GPX export is generated from persisted prediction segments, and the Garmin course push reuses the existing Python adapter, stored session, and operation gate.

**Tech Stack:** .NET 10, Blazor WebAssembly, ASP.NET Core Minimal APIs, EF Core with Npgsql, xUnit, bUnit, FastAPI with `garminconnect==0.3.4`, pytest.

**Authority:** `docs/superpowers/specs/2026-08-26-predictions-route-builder-design.md`.

---

## Orientation for the implementer

Read these before starting. They are the patterns every task below follows.

- `src/RouteTimer.Api/Endpoints/GarminEndpoints.cs` — how endpoints are grouped, mapped, and error-mapped.
- `src/RouteTimer.Api/Errors/ApiProblems.cs` — every failure returns a problem document from here. Available helpers: `Create(status, code, detail)`, `BadRequest`, `Conflict`, `Forbidden`, `NotFound`, `PayloadTooLarge`, `TooManyRequests`, `BadGateway`, `ServiceUnavailable`.
- `src/RouteTimer.Services/Garmin/GarminTokenProtection.cs` — the AES-GCM protector Task 8 generalises.
- `src/RouteTimer.Persistence/Repositories/GarminConnectionRepository.cs` — the single-row (`Id = 1`) repository convention every persisted entity in this repo follows.
- `src/RouteTimer.Client/Pages/Predictions.razor` — the page Task 12 modifies.
- `tests/RouteTimer.Client.Tests/PredictionsPageTests.cs` and `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs` — the bUnit and fake-client conventions.
- `garmin-adapter/src/routetimer_garmin/api.py`, `service.py`, `facade.py` — the three adapter layers Tasks 16–17 extend.

Full test command, run before every commit:

```bash
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false
```

Adapter test command, run before every commit that touches `garmin-adapter/`:

```bash
cd garmin-adapter && python -m pytest -q
```

---

## File Structure

**Created — client (Google Maps route builder, ported from MapToGarmin):**

| File | Responsibility |
| --- | --- |
| `src/RouteTimer.Client/RouteBuilder/Models/*.cs` | `RoutePoint`, `GpxWaypoint`, `RouteWaypoint`, `ParsedRoute`, `TravelMode` |
| `src/RouteTimer.Client/RouteBuilder/GoogleMapsUrlParser.cs` | Parses full Google Maps URLs into a `ParsedRoute` |
| `src/RouteTimer.Client/RouteBuilder/MapUrlParseException.cs` | Parse failure with a rider-readable message |
| `src/RouteTimer.Client/RouteBuilder/RouteGpxWriter.cs` | Writes the GPX submitted for prediction |
| `src/RouteTimer.Client/RouteBuilder/DirectionsInterop.cs` | Maps JavaScript API interop: load, route, elevate, scrub |
| `src/RouteTimer.Client/RouteBuilder/BrowserInterop.cs` | Origin, reload, clipboard |
| `src/RouteTimer.Client/RouteBuilder/ShortLinkClient.cs` | Calls the RouteTimer short-link endpoint |
| `src/RouteTimer.Client/Logging/*.cs` | `ActionLevel`, `LogEntry`, `KeyRedactor`, `ActionLog`, `JsLogBridge` |
| `src/RouteTimer.Client/Components/ActionLogView.razor` | Renders the action log with a copy action |
| `src/RouteTimer.Client/Components/GoogleMapsRouteInput.razor` | The Google Maps tab: key panel, URL, mode, convert |
| `src/RouteTimer.Client/wwwroot/js/gmaps.js` | Maps API loader, directions, elevation, teardown |
| `src/RouteTimer.Client/wwwroot/js/browser.js` | Origin, reload, clipboard helpers |

**Created — API and services:**

| File | Responsibility |
| --- | --- |
| `src/RouteTimer.Services/Security/SecretProtection.cs` | `ProtectedSecret`, `ISecretProtector`, `AesGcmSecretProtector` |
| `src/RouteTimer.Services/Settings/GoogleMapsKeyService.cs` | Store, read, reveal, delete the key |
| `src/RouteTimer.Services/Persistence/IGoogleMapsCredentialRepository.cs` | Single-row persistence contract |
| `src/RouteTimer.Persistence/Entities/GoogleMapsCredentialEntity.cs` | Encrypted key row |
| `src/RouteTimer.Persistence/Repositories/GoogleMapsCredentialRepository.cs` | Single-row upsert and delete |
| `src/RouteTimer.Services/Routes/ShortLinkResolutionService.cs` | Expands `maps.app.goo.gl` codes |
| `src/RouteTimer.Services/Routes/PredictionGpxWriter.cs` | Writes prediction GPX, timed and untimed |
| `src/RouteTimer.Services/Garmin/GarminCourseService.cs` | Orchestrates the course push |
| `src/RouteTimer.Api/Endpoints/SettingsEndpoints.cs` | Google Maps key endpoints |
| `src/RouteTimer.Api/Endpoints/RouteEndpoints.cs` | Short-link endpoint |
| `src/RouteTimer.Contracts/Settings/SettingsContracts.cs` | Key status, save, reveal contracts |
| `src/RouteTimer.Contracts/Routes/RouteContracts.cs` | Short-link response |

**Created — Python adapter:**

| File | Responsibility |
| --- | --- |
| `garmin-adapter/src/routetimer_garmin/courses.py` | Course payload construction and geometry maths |
| `garmin-adapter/tests/test_courses.py` | Payload and flow tests against a fake client |

**Modified:**

| File | Change |
| --- | --- |
| `src/RouteTimer.Client/Pages/Predictions.razor` | Two-mode submission panel |
| `src/RouteTimer.Client/Pages/PredictionDetail.razor` | GPX downloads, Send to Garmin |
| `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`, `RouteTimerApiClient.cs` | New client methods |
| `src/RouteTimer.Client/Program.cs` | Register route-builder services |
| `src/RouteTimer.Client/wwwroot/index.html` | Load `browser.js` module path only if needed |
| `src/RouteTimer.Contracts/Errors/ErrorCodes.cs` | New codes |
| `src/RouteTimer.Contracts/Predictions/PredictionContracts.cs` | Course id and timestamp on the summary |
| `src/RouteTimer.Services/Garmin/GarminTokenProtection.cs` | Delegates to the generalised protector |
| `src/RouteTimer.Services/Garmin/GarminAdapterContracts.cs` | `CreateCourseAsync` and its records |
| `src/RouteTimer.Api/Garmin/GarminAdapterClient.cs` | Implements `CreateCourseAsync` |
| `src/RouteTimer.Api/Endpoints/PredictionsEndpoints.cs` | GPX and course endpoints |
| `src/RouteTimer.Api/Program.cs` | New DI registrations and endpoint maps |
| `src/RouteTimer.Persistence/RouteTimerDbContext.cs` | New table and columns |
| `src/RouteTimer.Persistence/Entities/PredictionEntity.cs` | `GarminCourseId`, `GarminCourseUploadedAt` |
| `src/RouteTimer.Services/Persistence/IPredictionRepository.cs` | GPX source read, course id write |
| `garmin-adapter/src/routetimer_garmin/facade.py`, `service.py`, `api.py` | Course operation |
| `run.sh`, `run.ps1`, `deploy/docker-compose*.yml`, `RUNBOOK.md` | New encryption key |

---

## Task 1: Verify the Garmin course endpoints before building on them

The two endpoints this feature's last phase depends on are undocumented. Verify them once, by hand, before writing any of Phase 6. Phases 1 through 5 do not depend on this result and can proceed regardless.

**Files:**
- Create: `docs/garmin-course-spike.md`

- [ ] **Step 1: Start a Python shell with the adapter's dependencies**

```bash
cd garmin-adapter && python -m venv .venv-spike && .venv-spike/bin/pip install -q -r requirements.lock && .venv-spike/bin/python
```

- [ ] **Step 2: Log in and upload a small GPX as a course**

Use any small GPX file you already have. Do not use a rider's personal file from `Examples.zip`; export a short route from Garmin Connect or hand-write a ten-point track.

```python
from garminconnect import Garmin
g = Garmin("EMAIL", "PASSWORD")
g.login()

gpx = open("/path/to/short-route.gpx", "rb").read()
parsed = g.client.post(
    "connectapi",
    "/course-service/course/import",
    files={"file": ("short-route.gpx", gpx, "application/gpx+xml")},
    api=True,
)
print(type(parsed), list(parsed)[:20] if hasattr(parsed, "keys") else parsed)
print(len(parsed.get("geoPoints") or []))
```

Expected: a dict containing a non-empty `geoPoints` list, each entry carrying `latitude` and `longitude`.

- [ ] **Step 3: Save the parsed course**

```python
points = parsed["geoPoints"]
for i, p in enumerate(points):
    p["distance"] = 0.0 if i == 0 else p.get("distance") or 0.0
    if p.get("elevation") is None:
        p["elevation"] = 0.0

lats = [p["latitude"] for p in points]
lons = [p["longitude"] for p in points]
payload = {
    "courseName": "RouteTimer spike",
    "description": None,
    "openStreetMap": False,
    "matchedToSegments": False,
    "rulePK": 2,
    "sourceTypeId": 3,
    "distanceMeter": 0.0,
    "elevationGainMeter": 0.0,
    "elevationLossMeter": 0.0,
    "startPoint": {
        "latitude": points[0]["latitude"],
        "longitude": points[0]["longitude"],
        "elevation": points[0]["elevation"],
        "distance": None,
        "timestamp": None,
    },
    "coursePoints": [],
    "boundingBox": {
        "center": {"latitude": (min(lats) + max(lats)) / 2, "longitude": (min(lons) + max(lons)) / 2},
        "lowerLeft": {"latitude": min(lats), "longitude": min(lons)},
        "upperRight": {"latitude": max(lats), "longitude": max(lons)},
        "lowerLeftLatIsSet": True,
        "lowerLeftLongIsSet": True,
        "upperRightLatIsSet": True,
        "upperRightLongIsSet": True,
    },
    "hasShareableEvent": False,
    "hasTurnDetectionDisabled": False,
    "activityTypePk": 10,
    "includeLaps": False,
    "courseLines": [{
        "courseId": None,
        "sortOrder": 1,
        "numberOfPoints": len(points),
        "distanceInMeters": 0.0,
        "bearing": 0.0,
        "points": points,
        "coordinateSystem": "WGS84",
        "originalCoordinateSystem": "WGS84",
    }],
    "coordinateSystem": "WGS84",
    "targetCoordinateSystem": "WGS84",
    "originalCoordinateSystem": "WGS84",
    "elevationSource": 3,
    "hasPaceBand": False,
    "hasPowerGuide": False,
    "favorite": False,
    "geoPoints": points,
}
saved = g.client.post("connectapi", "/course-service/course", json=payload, api=True)
print(saved.get("courseId"), saved.get("courseName"), saved.get("distanceMeter"))
```

Expected: a numeric `courseId`, and the course visible at `https://connect.garmin.com/modern/course/<courseId>`.

- [ ] **Step 4: Record the outcome**

Write `docs/garmin-course-spike.md` with: the date, both endpoint paths, whether each returned what this step expected, the exact response keys observed on the import step, and the resulting course id. If either call failed, record the status code and body verbatim.

- [ ] **Step 5: Delete the spike course and the virtual environment**

```python
g.client.delete("connectapi", f"/course-service/course/{saved['courseId']}")
```

```bash
rm -rf garmin-adapter/.venv-spike
```

- [ ] **Step 6: Commit**

```bash
git add docs/garmin-course-spike.md
git commit -m "docs: record the Garmin course endpoint verification spike"
```

**Gate:** if Step 2 or Step 3 failed, stop before Task 16, report the recorded findings, and ship Tasks 2–15 alone. The rider's fallback is the GPX download from Task 14 plus a manual import in Garmin Connect.

---

## Task 2: Port the route models and the Google Maps URL parser

**Files:**
- Create: `src/RouteTimer.Client/RouteBuilder/Models/RoutePoint.cs`, `GpxWaypoint.cs`, `RouteWaypoint.cs`, `ParsedRoute.cs`, `TravelMode.cs`
- Create: `src/RouteTimer.Client/RouteBuilder/GoogleMapsUrlParser.cs`, `MapUrlParseException.cs`
- Test: `tests/RouteTimer.Client.Tests/RouteBuilder/GoogleMapsUrlParserTests.cs`

- [ ] **Step 1: Copy the sources and retarget their namespace**

```bash
mkdir -p src/RouteTimer.Client/RouteBuilder/Models tests/RouteTimer.Client.Tests/RouteBuilder
cp ~/RiderProjects/MapToGarmin/src/MapToGarmin/Models/*.cs src/RouteTimer.Client/RouteBuilder/Models/
cp ~/RiderProjects/MapToGarmin/src/MapToGarmin/Parsing/GoogleMapsUrlParser.cs src/RouteTimer.Client/RouteBuilder/
cp ~/RiderProjects/MapToGarmin/src/MapToGarmin/Parsing/MapUrlParseException.cs src/RouteTimer.Client/RouteBuilder/
sed -i '' 's/^namespace MapToGarmin\.Models;/namespace RouteTimer.Client.RouteBuilder.Models;/' src/RouteTimer.Client/RouteBuilder/Models/*.cs
sed -i '' 's/^namespace MapToGarmin\.Parsing;/namespace RouteTimer.Client.RouteBuilder;/' src/RouteTimer.Client/RouteBuilder/*.cs
sed -i '' 's/^using MapToGarmin\.Models;/using RouteTimer.Client.RouteBuilder.Models;/' src/RouteTimer.Client/RouteBuilder/*.cs
```

- [ ] **Step 2: Copy the parser tests and retarget them**

```bash
cp ~/RiderProjects/MapToGarmin/tests/MapToGarmin.Tests/GoogleMapsUrlParserTests.cs tests/RouteTimer.Client.Tests/RouteBuilder/
sed -i '' 's/^namespace MapToGarmin\.Tests;/namespace RouteTimer.Client.Tests.RouteBuilder;/' tests/RouteTimer.Client.Tests/RouteBuilder/GoogleMapsUrlParserTests.cs
sed -i '' 's/^using MapToGarmin\.Parsing;/using RouteTimer.Client.RouteBuilder;/;s/^using MapToGarmin\.Models;/using RouteTimer.Client.RouteBuilder.Models;/' tests/RouteTimer.Client.Tests/RouteBuilder/GoogleMapsUrlParserTests.cs
```

- [ ] **Step 3: Run the ported tests**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~GoogleMapsUrlParserTests`
Expected: PASS. If a test fails on a namespace or `using`, fix the namespace — do not change an assertion. These tests encode Google's URL formats and are the reason the port is worth doing.

- [ ] **Step 4: Commit**

```bash
git add src/RouteTimer.Client/RouteBuilder tests/RouteTimer.Client.Tests/RouteBuilder
git commit -m "feat(client): port the Google Maps URL parser and route models"
```

---

## Task 3: Port the action log and its redaction

**Files:**
- Create: `src/RouteTimer.Client/Logging/LogEntry.cs`, `KeyRedactor.cs`, `ActionLog.cs`, `JsLogBridge.cs`
- Create: `src/RouteTimer.Client/Components/ActionLogView.razor`
- Test: `tests/RouteTimer.Client.Tests/Logging/ActionLogTests.cs`, `KeyRedactorTests.cs`

- [ ] **Step 1: Copy the sources and tests, retargeting namespaces**

```bash
mkdir -p src/RouteTimer.Client/Logging tests/RouteTimer.Client.Tests/Logging
cp ~/RiderProjects/MapToGarmin/src/MapToGarmin/Logging/*.cs src/RouteTimer.Client/Logging/
cp ~/RiderProjects/MapToGarmin/src/MapToGarmin/Services/JsLogBridge.cs src/RouteTimer.Client/Logging/
cp ~/RiderProjects/MapToGarmin/src/MapToGarmin/Components/LogView.razor src/RouteTimer.Client/Components/ActionLogView.razor
cp ~/RiderProjects/MapToGarmin/tests/MapToGarmin.Tests/ActionLogTests.cs ~/RiderProjects/MapToGarmin/tests/MapToGarmin.Tests/KeyRedactorTests.cs tests/RouteTimer.Client.Tests/Logging/
sed -i '' 's/^namespace MapToGarmin\.Logging;/namespace RouteTimer.Client.Logging;/;s/^namespace MapToGarmin\.Services;/namespace RouteTimer.Client.Logging;/' src/RouteTimer.Client/Logging/*.cs
sed -i '' 's/^using MapToGarmin\.Logging;/using RouteTimer.Client.Logging;/' src/RouteTimer.Client/Logging/*.cs
sed -i '' 's/^namespace MapToGarmin\.Tests;/namespace RouteTimer.Client.Tests.Logging;/;s/^using MapToGarmin\.Logging;/using RouteTimer.Client.Logging;/' tests/RouteTimer.Client.Tests/Logging/*.cs
```

- [ ] **Step 2: Retarget the component**

In `src/RouteTimer.Client/Components/ActionLogView.razor`, replace `@using MapToGarmin.Logging` with `@using RouteTimer.Client.Logging`, and rename the component's usages from `LogView` to `ActionLogView` if the file references its own name. Add `data-testid="route-builder-log"` to the outermost element so bUnit tests in Task 12 can find it.

- [ ] **Step 3: Run the ported tests**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter "FullyQualifiedName~ActionLogTests|FullyQualifiedName~KeyRedactorTests"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/RouteTimer.Client/Logging src/RouteTimer.Client/Components/ActionLogView.razor tests/RouteTimer.Client.Tests/Logging
git commit -m "feat(client): port the action log, key redaction, and log view"
```

---

## Task 4: Port the route GPX writer

**Files:**
- Create: `src/RouteTimer.Client/RouteBuilder/RouteGpxWriter.cs`
- Test: `tests/RouteTimer.Client.Tests/RouteBuilder/RouteGpxWriterTests.cs`

- [ ] **Step 1: Copy the writer and its tests**

```bash
cp ~/RiderProjects/MapToGarmin/src/MapToGarmin/Gpx/GpxWriter.cs src/RouteTimer.Client/RouteBuilder/RouteGpxWriter.cs
cp ~/RiderProjects/MapToGarmin/tests/MapToGarmin.Tests/GpxWriterTests.cs tests/RouteTimer.Client.Tests/RouteBuilder/RouteGpxWriterTests.cs
sed -i '' 's/^namespace MapToGarmin\.Gpx;/namespace RouteTimer.Client.RouteBuilder;/;s/^using MapToGarmin\.Models;/using RouteTimer.Client.RouteBuilder.Models;/;s/\bGpxWriter\b/RouteGpxWriter/g' src/RouteTimer.Client/RouteBuilder/RouteGpxWriter.cs
sed -i '' 's/^namespace MapToGarmin\.Tests;/namespace RouteTimer.Client.Tests.RouteBuilder;/;s/^using MapToGarmin\.Gpx;/using RouteTimer.Client.RouteBuilder;/;s/^using MapToGarmin\.Models;/using RouteTimer.Client.RouteBuilder.Models;/;s/\bGpxWriter\b/RouteGpxWriter/g;s/\bGpxWriterTests\b/RouteGpxWriterTests/g' tests/RouteTimer.Client.Tests/RouteBuilder/RouteGpxWriterTests.cs
```

- [ ] **Step 2: Change the creator attribute**

In `RouteGpxWriter.cs`, change `writer.WriteAttributeString("creator", "MapToGarmin");` to `writer.WriteAttributeString("creator", "RouteTimer");`. Update the corresponding assertion in `RouteGpxWriterTests.cs` if one exists.

- [ ] **Step 3: Write a failing test for elevation completeness**

Add to `tests/RouteTimer.Client.Tests/RouteBuilder/RouteGpxWriterTests.cs`:

```csharp
[Fact]
public void Every_track_point_carries_elevation_when_elevation_is_present()
{
    var track = new[]
    {
        new RoutePoint(51.5000000, -0.1000000, 12.3),
        new RoutePoint(51.5010000, -0.1010000, 15.8)
    };

    var gpx = RouteGpxWriter.Write("Test route", [], track, DateTimeOffset.UnixEpoch);

    Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(gpx, "<ele>").Count);
    Assert.Contains("<ele>12.3</ele>", gpx, StringComparison.Ordinal);
    Assert.Contains("creator=\"RouteTimer\"", gpx, StringComparison.Ordinal);
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RouteGpxWriterTests`
Expected: PASS. The writer already emits `ele` for points that carry one; this test pins that behaviour before Task 5 makes elevation mandatory.

- [ ] **Step 5: Commit**

```bash
git add src/RouteTimer.Client/RouteBuilder/RouteGpxWriter.cs tests/RouteTimer.Client.Tests/RouteBuilder/RouteGpxWriterTests.cs
git commit -m "feat(client): port the route GPX writer"
```

---

## Task 5: Port the Google Maps JavaScript interop

**Files:**
- Create: `src/RouteTimer.Client/RouteBuilder/DirectionsInterop.cs`, `BrowserInterop.cs`
- Create: `src/RouteTimer.Client/wwwroot/js/gmaps.js`, `src/RouteTimer.Client/wwwroot/js/browser.js`
- Test: `tests/RouteTimer.Client.Tests/RouteBuilder/ElevationInterpolationTests.cs`

- [ ] **Step 1: Copy the interop sources, scripts, and interpolation tests**

```bash
cp ~/RiderProjects/MapToGarmin/src/MapToGarmin/Services/DirectionsInterop.cs ~/RiderProjects/MapToGarmin/src/MapToGarmin/Services/BrowserInterop.cs src/RouteTimer.Client/RouteBuilder/
cp ~/RiderProjects/MapToGarmin/src/MapToGarmin/wwwroot/js/gmaps.js ~/RiderProjects/MapToGarmin/src/MapToGarmin/wwwroot/js/browser.js src/RouteTimer.Client/wwwroot/js/
cp ~/RiderProjects/MapToGarmin/tests/MapToGarmin.Tests/ElevationInterpolationTests.cs tests/RouteTimer.Client.Tests/RouteBuilder/
sed -i '' 's/^namespace MapToGarmin\.Services;/namespace RouteTimer.Client.RouteBuilder;/;s/^using MapToGarmin\.Logging;/using RouteTimer.Client.Logging;/;s/^using MapToGarmin\.Models;/using RouteTimer.Client.RouteBuilder.Models;/' src/RouteTimer.Client/RouteBuilder/DirectionsInterop.cs src/RouteTimer.Client/RouteBuilder/BrowserInterop.cs
sed -i '' 's/^namespace MapToGarmin\.Tests;/namespace RouteTimer.Client.Tests.RouteBuilder;/;s/^using MapToGarmin\.Services;/using RouteTimer.Client.RouteBuilder;/;s/^using MapToGarmin\.Models;/using RouteTimer.Client.RouteBuilder.Models;/' tests/RouteTimer.Client.Tests/RouteBuilder/ElevationInterpolationTests.cs
```

`browser.js` keeps `origin`, `reload`, and `copyToClipboard`; RouteTimer never uses its `download` helper, because Task 15 downloads prediction GPX through a plain anchor to the API instead. Delete the `download` export from `browser.js` and the `DownloadAsync` method from `BrowserInterop.cs`.

- [ ] **Step 2: Write a failing test for the elevation-gap detector**

Elevation is mandatory in RouteTimer, so the interop must report whether every point got one. Add to `tests/RouteTimer.Client.Tests/RouteBuilder/ElevationInterpolationTests.cs`:

```csharp
[Fact]
public void Elevation_completeness_is_false_when_any_point_lacks_elevation()
{
    IReadOnlyList<RoutePoint> complete =
    [
        new RoutePoint(51.5, -0.1, 10),
        new RoutePoint(51.6, -0.2, 20)
    ];
    IReadOnlyList<RoutePoint> partial =
    [
        new RoutePoint(51.5, -0.1, 10),
        new RoutePoint(51.6, -0.2)
    ];

    Assert.True(DirectionsInterop.HasCompleteElevation(complete));
    Assert.False(DirectionsInterop.HasCompleteElevation(partial));
    Assert.False(DirectionsInterop.HasCompleteElevation([]));
}
```

- [ ] **Step 3: Run it and watch it fail**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~ElevationInterpolationTests`
Expected: FAIL — `DirectionsInterop` does not contain a definition for `HasCompleteElevation`.

- [ ] **Step 4: Add the method**

In `src/RouteTimer.Client/RouteBuilder/DirectionsInterop.cs`, add:

```csharp
// RouteTimer's predictor derives gradient from elevation, so a track missing any elevation
// produces a confident and wrong answer. MapToGarmin tolerated this because a navigation
// course does not need elevation; a prediction does.
public static bool HasCompleteElevation(IReadOnlyList<RoutePoint> path) =>
    path.Count > 0 && path.All(point => point.Elevation is not null);
```

Add `using System.Linq;` if the file does not already have it.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~ElevationInterpolationTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/RouteTimer.Client/RouteBuilder src/RouteTimer.Client/wwwroot/js tests/RouteTimer.Client.Tests/RouteBuilder
git commit -m "feat(client): port the Google Maps directions and elevation interop"
```

---

## Task 6: Add the short-link expansion endpoint

**Files:**
- Create: `src/RouteTimer.Services/Routes/ShortLinkResolutionService.cs`
- Create: `src/RouteTimer.Contracts/Routes/RouteContracts.cs`
- Create: `src/RouteTimer.Api/Endpoints/RouteEndpoints.cs`
- Modify: `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`, `src/RouteTimer.Api/Program.cs`
- Test: `tests/RouteTimer.Services.Tests/Routes/ShortLinkResolutionServiceTests.cs`

- [ ] **Step 1: Write the failing service tests**

```csharp
using System.Net;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Tests.Routes;

public sealed class ShortLinkResolutionServiceTests
{
    [Theory]
    [InlineData(HttpStatusCode.Moved)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Returns_the_location_of_a_redirect(HttpStatusCode status)
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("https://www.google.com/maps/dir/A/B");
            return response;
        });
        var service = CreateService(handler);

        var resolved = await service.ResolveAsync("abcd1234", CancellationToken.None);

        Assert.Equal("https://www.google.com/maps/dir/A/B", resolved);
        Assert.Equal("/abcd1234", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("RouteTimer/1.0", handler.LastRequest.Headers.UserAgent.ToString());
        Assert.False(handler.LastRequest.Headers.Contains("Cookie"));
        Assert.Null(handler.LastRequest.Headers.Referrer);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("")]
    public async Task Rejects_a_code_that_does_not_match_the_permitted_shape(string code)
    {
        var service = CreateService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)));

        await Assert.ThrowsAsync<ShortLinkCodeInvalidException>(
            () => service.ResolveAsync(code, CancellationToken.None));
    }

    [Fact]
    public async Task Fails_when_the_upstream_does_not_redirect()
    {
        var service = CreateService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        await Assert.ThrowsAsync<ShortLinkUnresolvedException>(
            () => service.ResolveAsync("abcd1234", CancellationToken.None));
    }

    [Fact]
    public async Task Fails_when_a_redirect_carries_no_location()
    {
        var service = CreateService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)));

        await Assert.ThrowsAsync<ShortLinkUnresolvedException>(
            () => service.ResolveAsync("abcd1234", CancellationToken.None));
    }

    private static ShortLinkResolutionService CreateService(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://maps.app.goo.gl") });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~ShortLinkResolutionServiceTests`
Expected: FAIL — the type `ShortLinkResolutionService` does not exist.

- [ ] **Step 3: Write the service**

`src/RouteTimer.Services/Routes/ShortLinkResolutionService.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;

namespace RouteTimer.Services.Routes;

public sealed class ShortLinkCodeInvalidException() : Exception("The short-link code is not in the permitted form.");

public sealed class ShortLinkUnresolvedException() : Exception("The short link did not resolve to a Google Maps URL.");

public sealed partial class ShortLinkResolutionService(HttpClient httpClient)
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{4,64}$")]
    private static partial Regex PermittedCode();

    public async Task<string> ResolveAsync(string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || !PermittedCode().IsMatch(code))
        {
            throw new ShortLinkCodeInvalidException();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/{code}");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
        {
            throw new ShortLinkUnresolvedException();
        }

        return response.Headers.Location.ToString();
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.Moved or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~ShortLinkResolutionServiceTests`
Expected: PASS.

- [ ] **Step 5: Add the contract and the error codes**

`src/RouteTimer.Contracts/Routes/RouteContracts.cs`:

```csharp
namespace RouteTimer.Contracts.Routes;

public sealed record ShortLinkResponse(string ResolvedUrl);
```

Add to `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`, beside the existing constants:

```csharp
    public const string ShortLinkCodeInvalid = "short-link-code-invalid";
    public const string ShortLinkUnresolved = "short-link-unresolved";
```

- [ ] **Step 6: Add the endpoint**

`src/RouteTimer.Api/Endpoints/RouteEndpoints.cs`:

```csharp
using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Routes;
using RouteTimer.Services.Routes;

namespace RouteTimer.Api.Endpoints;

public static class RouteEndpoints
{
    public static IEndpointRouteBuilder MapRouteEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/routes/short-links/{code}", ResolveShortLinkAsync);
        return routes;
    }

    private static async Task<IResult> ResolveShortLinkAsync(
        string code,
        ShortLinkResolutionService shortLinks,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await shortLinks.ResolveAsync(code, cancellationToken);
            return TypedResults.Ok(new ShortLinkResponse(resolved));
        }
        catch (ShortLinkCodeInvalidException)
        {
            return ApiProblems.BadRequest(
                ErrorCodes.ShortLinkCodeInvalid,
                "The short-link code is not in the permitted form.");
        }
        catch (ShortLinkUnresolvedException)
        {
            return ApiProblems.BadGateway(
                ErrorCodes.ShortLinkUnresolved,
                "The short link did not resolve. Open it in a browser tab and paste the expanded Google Maps URL instead.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiProblems.Create(
                StatusCodes.Status504GatewayTimeout,
                ErrorCodes.ShortLinkUnresolved,
                "The short-link service did not respond in time.");
        }
    }
}
```

- [ ] **Step 7: Register the client and the endpoint**

In `src/RouteTimer.Api/Program.cs`, beside the other service registrations:

```csharp
// maps.app.goo.gl sends no Access-Control-Allow-Origin and is cross-origin-resource-policy:
// same-site, so the browser cannot expand a short link itself. Redirects are not followed: the
// Location header is the entire answer, and following it would fetch Google on the rider's behalf.
// A browser-like User-Agent makes the endpoint answer 200 with a JavaScript interstitial and no
// Location header, so this deliberately non-browser agent is load-bearing.
builder.Services.AddHttpClient<ShortLinkResolutionService>(client =>
{
    client.BaseAddress = new Uri("https://maps.app.goo.gl");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("RouteTimer/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false
});
```

And beside `app.MapPredictionEndpoints();`:

```csharp
app.MapRouteEndpoints();
```

- [ ] **Step 8: Add an endpoint test**

`tests/RouteTimer.Api.Tests/Endpoints/RouteEndpointsTests.cs`, following the shape of the existing endpoint tests and `RouteTimerApiFactory`: assert that a code failing the pattern returns `400` with `ErrorCodes.ShortLinkCodeInvalid`, and that a stubbed redirect returns `200` with the resolved URL. Register the stub by replacing the `ShortLinkResolutionService`'s primary handler in the factory's service overrides.

- [ ] **Step 9: Run the full suite and commit**

Run: `dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false`
Expected: PASS.

```bash
git add src/RouteTimer.Services/Routes/ShortLinkResolutionService.cs src/RouteTimer.Contracts src/RouteTimer.Api tests
git commit -m "feat(api): expand Google Maps short links through a first-party route"
```

---

## Task 7: Call the short-link endpoint from the client

**Files:**
- Create: `src/RouteTimer.Client/RouteBuilder/ShortLinkClient.cs`
- Modify: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`, `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`, `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`
- Test: `tests/RouteTimer.Client.Tests/RouteBuilder/ShortLinkClientTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RouteTimer.Client.Logging;
using RouteTimer.Client.RouteBuilder;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Routes;

namespace RouteTimer.Client.Tests.RouteBuilder;

public sealed class ShortLinkClientTests
{
    [Fact]
    public async Task Returns_the_resolved_url_and_logs_it()
    {
        var api = new FakeRouteTimerApiClient
        {
            OnResolveShortLinkAsync = (code, _) =>
                Task.FromResult(new ShortLinkResponse($"https://www.google.com/maps/dir/{code}"))
        };
        var log = new ActionLog();
        var client = new ShortLinkClient(api, log);

        var resolved = await client.ResolveAsync("abcd1234", CancellationToken.None);

        Assert.Equal("https://www.google.com/maps/dir/abcd1234", resolved);
        Assert.Contains(log.Entries, entry => entry.Level == ActionLevel.Success);
    }

    [Fact]
    public async Task Returns_null_and_explains_the_manual_work_around_on_failure()
    {
        var api = new FakeRouteTimerApiClient
        {
            OnResolveShortLinkAsync = (_, _) => throw new HttpRequestException("boom")
        };
        var log = new ActionLog();
        var client = new ShortLinkClient(api, log);

        var resolved = await client.ResolveAsync("abcd1234", CancellationToken.None);

        Assert.Null(resolved);
        Assert.Contains(log.Entries, entry =>
            entry.Level == ActionLevel.Warn &&
            entry.Detail is not null &&
            entry.Detail.Contains("paste", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~ShortLinkClientTests`
Expected: FAIL — `ShortLinkClient` and `OnResolveShortLinkAsync` do not exist.

- [ ] **Step 3: Add the API client method**

In `IRouteTimerApiClient.cs`:

```csharp
    Task<ShortLinkResponse> ResolveShortLinkAsync(string code, CancellationToken ct);
```

In `RouteTimerApiClient.cs`, following the existing `GetRequiredAsync` usages:

```csharp
    public Task<ShortLinkResponse> ResolveShortLinkAsync(string code, CancellationToken ct) =>
        GetRequiredAsync<ShortLinkResponse>($"/api/routes/short-links/{Uri.EscapeDataString(code)}", ct);
```

In `FakeRouteTimerApiClient.cs`, add the delegate property and its implementation, matching the file's existing style:

```csharp
    public Func<string, CancellationToken, Task<ShortLinkResponse>>? OnResolveShortLinkAsync { get; set; }

    public Task<ShortLinkResponse> ResolveShortLinkAsync(string code, CancellationToken ct) =>
        OnResolveShortLinkAsync?.Invoke(code, ct) ?? throw new NotImplementedException();
```

- [ ] **Step 4: Write the client**

`src/RouteTimer.Client/RouteBuilder/ShortLinkClient.cs`:

```csharp
using RouteTimer.Client.Api;
using RouteTimer.Client.Logging;

namespace RouteTimer.Client.RouteBuilder;

public sealed class ShortLinkClient(IRouteTimerApiClient api, ActionLog log)
{
    private const string ManualWorkAround =
        "Open the short link in a browser tab, copy the full www.google.com/maps URL it lands on, " +
        "and paste that into the same URL box.";

    public async Task<string?> ResolveAsync(string code, CancellationToken cancellationToken)
    {
        log.Info(
            $"Expanding short link '{code}' through RouteTimer's own API.",
            "The browser cannot fetch maps.app.goo.gl directly. Only the code is sent, never the API key.");

        try
        {
            var response = await api.ResolveShortLinkAsync(code, cancellationToken);
            log.Success("Short link resolved.", response.ResolvedUrl);
            return response.ResolvedUrl;
        }
        catch (ApiProblemException problem)
        {
            log.Warn($"RouteTimer could not expand the short link: {problem.Message}", ManualWorkAround);
            return null;
        }
        catch (HttpRequestException exception)
        {
            log.Warn($"Could not reach RouteTimer to expand the short link: {exception.Message}", ManualWorkAround);
            return null;
        }
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~ShortLinkClientTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat(client): resolve Google Maps short links through the RouteTimer API"
```

---

## Task 8: Generalise the AES-GCM protector

**Files:**
- Create: `src/RouteTimer.Services/Security/SecretProtection.cs`
- Modify: `src/RouteTimer.Services/Garmin/GarminTokenProtection.cs`
- Test: `tests/RouteTimer.Services.Tests/Security/AesGcmSecretProtectorTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Security.Cryptography;
using RouteTimer.Services.Garmin;
using RouteTimer.Services.Security;

namespace RouteTimer.Services.Tests.Security;

public sealed class AesGcmSecretProtectorTests
{
    private static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Round_trips_a_secret()
    {
        using var protector = new AesGcmSecretProtector(Key(), "RouteTimer:Test:1:1");

        var protectedSecret = protector.Protect("a-secret-value");

        Assert.Equal("a-secret-value", protector.Unprotect(protectedSecret));
    }

    [Fact]
    public void A_ciphertext_written_for_one_purpose_does_not_decrypt_under_another()
    {
        var key = Key();
        using var writer = new AesGcmSecretProtector(key, "RouteTimer:PurposeA:1:1");
        using var reader = new AesGcmSecretProtector(key, "RouteTimer:PurposeB:1:1");

        var protectedSecret = writer.Protect("a-secret-value");

        Assert.Throws<AuthenticationTagMismatchException>(() => reader.Unprotect(protectedSecret));
    }

    [Fact]
    public void The_garmin_protector_still_reads_its_existing_additional_data()
    {
        var key = Key();
        using var garmin = new AesGcmGarminTokenProtector(key);
        using var equivalent = new AesGcmSecretProtector(key, "RouteTimer:GarminToken:1:1");

        var token = garmin.Protect("{\"token\":\"value\"}");
        var asSecret = new ProtectedSecret(token.Version, token.Nonce, token.Ciphertext, token.Tag);

        Assert.Equal("{\"token\":\"value\"}", equivalent.Unprotect(asSecret));
    }

    [Fact]
    public void Rejects_a_key_that_is_not_thirty_two_bytes()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new byte[16], "RouteTimer:Test:1:1"));
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~AesGcmSecretProtectorTests`
Expected: FAIL — the type `AesGcmSecretProtector` does not exist.

- [ ] **Step 3: Write the generalised protector**

`src/RouteTimer.Services/Security/SecretProtection.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace RouteTimer.Services.Security;

public sealed record ProtectedSecret(int Version, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

public interface ISecretProtector
{
    ProtectedSecret Protect(string plaintext);

    string Unprotect(ProtectedSecret protectedSecret);
}

/// <summary>
/// AES-GCM protection for a single class of secret. The purpose string becomes the additional
/// authenticated data, so a ciphertext written for one purpose cannot be decrypted as another even
/// under the same key.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector, IDisposable
{
    public const int EncryptionVersion = 1;
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly byte[] key;
    private readonly byte[] additionalData;
    private bool disposed;

    public AesGcmSecretProtector(byte[] key, string purpose)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (key.Length != KeyLength)
        {
            throw new ArgumentException("The secret protection key must be 32 bytes.", nameof(key));
        }

        this.key = key.ToArray();
        additionalData = Encoding.UTF8.GetBytes(purpose);
    }

    public ProtectedSecret Protect(string plaintext)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var ciphertext = new byte[bytes.Length];
            var tag = new byte[TagLength];
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, bytes, ciphertext, tag, additionalData);
            return new ProtectedSecret(EncryptionVersion, nonce, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string Unprotect(ProtectedSecret protectedSecret)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(protectedSecret);
        Validate(protectedSecret);

        var plaintext = new byte[protectedSecret.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(
                protectedSecret.Nonce,
                protectedSecret.Ciphertext,
                protectedSecret.Tag,
                plaintext,
                additionalData);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(key);
        disposed = true;
    }

    private static void Validate(ProtectedSecret protectedSecret)
    {
        if (protectedSecret.Version != EncryptionVersion)
        {
            throw new ArgumentException("Unsupported secret encryption version.", nameof(protectedSecret));
        }

        if (protectedSecret.Nonce is null || protectedSecret.Nonce.Length != NonceLength ||
            protectedSecret.Ciphertext is null || protectedSecret.Ciphertext.Length == 0 ||
            protectedSecret.Tag is null || protectedSecret.Tag.Length != TagLength)
        {
            throw new ArgumentException("The protected secret has an invalid AES-GCM shape.", nameof(protectedSecret));
        }
    }
}
```

- [ ] **Step 4: Make the Garmin protector delegate to it**

Replace the body of `AesGcmGarminTokenProtector` in `src/RouteTimer.Services/Garmin/GarminTokenProtection.cs`, keeping the class, the `ProtectedGarminToken` record, and the `IGarminTokenProtector` interface exactly as they are:

```csharp
using RouteTimer.Services.Security;

namespace RouteTimer.Services.Garmin;

public sealed record ProtectedGarminToken(int Version, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

public interface IGarminTokenProtector
{
    ProtectedGarminToken Protect(string tokenJson);

    string Unprotect(ProtectedGarminToken protectedToken);
}

public sealed class AesGcmGarminTokenProtector : IGarminTokenProtector, IDisposable
{
    // Load-bearing and frozen. Every Garmin token already in the database was sealed with this
    // exact additional authenticated data; changing a single byte makes all of them undecryptable.
    private const string Purpose = "RouteTimer:GarminToken:1:1";
    private readonly AesGcmSecretProtector inner;

    public AesGcmGarminTokenProtector(byte[] key) => inner = new AesGcmSecretProtector(key, Purpose);

    public ProtectedGarminToken Protect(string tokenJson)
    {
        var secret = inner.Protect(tokenJson);
        return new ProtectedGarminToken(secret.Version, secret.Nonce, secret.Ciphertext, secret.Tag);
    }

    public string Unprotect(ProtectedGarminToken protectedToken)
    {
        ArgumentNullException.ThrowIfNull(protectedToken);
        return inner.Unprotect(new ProtectedSecret(
            protectedToken.Version,
            protectedToken.Nonce,
            protectedToken.Ciphertext,
            protectedToken.Tag));
    }

    public void Dispose() => inner.Dispose();
}
```

- [ ] **Step 5: Run the security and Garmin tests**

Run: `dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter "FullyQualifiedName~Security|FullyQualifiedName~Garmin"`
Expected: PASS, including every pre-existing Garmin token protection test unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/RouteTimer.Services/Security src/RouteTimer.Services/Garmin/GarminTokenProtection.cs tests/RouteTimer.Services.Tests/Security
git commit -m "refactor(services): generalise AES-GCM protection behind a purpose string"
```

---

## Task 9: Persist the encrypted Google Maps key

**Files:**
- Create: `src/RouteTimer.Persistence/Entities/GoogleMapsCredentialEntity.cs`
- Create: `src/RouteTimer.Services/Persistence/IGoogleMapsCredentialRepository.cs`
- Create: `src/RouteTimer.Persistence/Repositories/GoogleMapsCredentialRepository.cs`
- Modify: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`
- Test: `tests/RouteTimer.Persistence.Tests/GoogleMapsCredentialRepositoryTests.cs`

- [ ] **Step 1: Write the failing repository tests**

Follow the fixture pattern the existing `tests/RouteTimer.Persistence.Tests` files use for a real PostgreSQL context.

```csharp
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Security;

namespace RouteTimer.Persistence.Tests;

public sealed class GoogleMapsCredentialRepositoryTests // : inherit the project's database fixture
{
    [Fact]
    public async Task Returns_null_when_no_key_is_stored()
    {
        var repository = CreateRepository();

        Assert.Null(await repository.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Saves_and_replaces_the_single_row()
    {
        var repository = CreateRepository();
        var first = new GoogleMapsCredentialRecord(
            new ProtectedSecret(1, new byte[12], [1, 2, 3], new byte[16]),
            "aaaa…zzzz",
            DateTimeOffset.UnixEpoch);
        var second = first with
        {
            Secret = new ProtectedSecret(1, new byte[12], [4, 5, 6], new byte[16]),
            KeyHint = "bbbb…yyyy"
        };

        await repository.SaveAsync(first, CancellationToken.None);
        await repository.SaveAsync(second, CancellationToken.None);

        var stored = await repository.GetAsync(CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("bbbb…yyyy", stored.KeyHint);
        Assert.Equal(new byte[] { 4, 5, 6 }, stored.Secret.Ciphertext);
    }

    [Fact]
    public async Task Deletes_the_stored_key_and_tolerates_a_missing_one()
    {
        var repository = CreateRepository();

        await repository.DeleteAsync(CancellationToken.None);
        await repository.SaveAsync(
            new GoogleMapsCredentialRecord(
                new ProtectedSecret(1, new byte[12], [1], new byte[16]),
                "aaaa…zzzz",
                DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        await repository.DeleteAsync(CancellationToken.None);

        Assert.Null(await repository.GetAsync(CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/RouteTimer.Persistence.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~GoogleMapsCredentialRepositoryTests`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Add the entity, the contract, and the repository**

`src/RouteTimer.Persistence/Entities/GoogleMapsCredentialEntity.cs`:

```csharp
namespace RouteTimer.Persistence.Entities;

public sealed class GoogleMapsCredentialEntity
{
    public int Id { get; set; }
    public int EncryptionVersion { get; set; }
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public string KeyHint { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`src/RouteTimer.Services/Persistence/IGoogleMapsCredentialRepository.cs`:

```csharp
using RouteTimer.Services.Security;

namespace RouteTimer.Services.Persistence;

public sealed record GoogleMapsCredentialRecord(ProtectedSecret Secret, string KeyHint, DateTimeOffset UpdatedAt);

public interface IGoogleMapsCredentialRepository
{
    Task<GoogleMapsCredentialRecord?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(GoogleMapsCredentialRecord credential, CancellationToken cancellationToken);

    Task DeleteAsync(CancellationToken cancellationToken);
}
```

`src/RouteTimer.Persistence/Repositories/GoogleMapsCredentialRepository.cs` mirrors `GarminConnectionRepository` exactly: a `private const int CredentialId = 1;`, `SingleOrDefaultAsync` on that id in all three methods, `AsNoTracking` on the read, add-then-update on save, and no-op on a missing delete.

- [ ] **Step 4: Register the set and its mapping**

In `src/RouteTimer.Persistence/RouteTimerDbContext.cs`, add `public DbSet<GoogleMapsCredentialEntity> GoogleMapsCredentials => Set<GoogleMapsCredentialEntity>();` and, in `OnModelCreating`, map it to table `google_maps_credentials` with `Id` as the key, `KeyHint` limited to 64 characters, and the three byte arrays required — matching how `GarminConnectionEntity` is mapped in the same method.

- [ ] **Step 5: Create the migration**

Run:

```bash
dotnet ef migrations add AddGoogleMapsCredential --project src/RouteTimer.Persistence/RouteTimer.Persistence.csproj --startup-project src/RouteTimer.Api/RouteTimer.Api.csproj
```

Then confirm the model and the migrations agree:

```bash
dotnet ef migrations has-pending-model-changes --project src/RouteTimer.Persistence/RouteTimer.Persistence.csproj --startup-project src/RouteTimer.Api/RouteTimer.Api.csproj
```

Expected: "No changes have been made to the model since the last migration."

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/RouteTimer.Persistence.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~GoogleMapsCredentialRepositoryTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/RouteTimer.Persistence src/RouteTimer.Services/Persistence tests/RouteTimer.Persistence.Tests
git commit -m "feat(persistence): store the encrypted Google Maps API key"
```

---

## Task 10: Add the Google Maps key service and endpoints

**Files:**
- Create: `src/RouteTimer.Services/Settings/GoogleMapsKeyService.cs`
- Create: `src/RouteTimer.Contracts/Settings/SettingsContracts.cs`
- Create: `src/RouteTimer.Api/Endpoints/SettingsEndpoints.cs`
- Modify: `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`, `src/RouteTimer.Api/Program.cs`
- Test: `tests/RouteTimer.Services.Tests/Settings/GoogleMapsKeyServiceTests.cs`, `tests/RouteTimer.Api.Tests/Endpoints/SettingsEndpointsTests.cs`

- [ ] **Step 1: Write the failing service tests**

```csharp
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Security;
using RouteTimer.Services.Settings;
using Microsoft.Extensions.Time.Testing;

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
```

Write `FakeGoogleMapsCredentialRepository` as an in-memory single-field implementation of `IGoogleMapsCredentialRepository` in the same test folder.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~GoogleMapsKeyServiceTests`
Expected: FAIL — the type `GoogleMapsKeyService` does not exist.

- [ ] **Step 3: Write the service**

`src/RouteTimer.Services/Settings/GoogleMapsKeyService.cs`:

```csharp
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Security;

namespace RouteTimer.Services.Settings;

public sealed class GoogleMapsKeyStorageUnavailableException()
    : Exception("Google Maps key storage is not configured on this deployment.");

public sealed class GoogleMapsKeyNotStoredException()
    : Exception("No Google Maps API key is stored.");

public sealed class GoogleMapsKeyInvalidException()
    : Exception("The Google Maps API key is empty or too long.");

public sealed record GoogleMapsKeyStatus(bool Configured, string? Hint, bool StorageAvailable);

public sealed class GoogleMapsKeyService(
    IGoogleMapsCredentialRepository repository,
    ISecretProtector? protector,
    TimeProvider timeProvider)
{
    public const string Purpose = "RouteTimer:GoogleMapsKey:1:1";
    private const int MaximumKeyLength = 512;
    private const int MinimumMaskableLength = 8;

    public async Task<GoogleMapsKeyStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (protector is null)
        {
            return new GoogleMapsKeyStatus(false, null, false);
        }

        var stored = await repository.GetAsync(cancellationToken);
        return new GoogleMapsKeyStatus(stored is not null, stored?.KeyHint, true);
    }

    public async Task SaveAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (protector is null)
        {
            throw new GoogleMapsKeyStorageUnavailableException();
        }

        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > MaximumKeyLength)
        {
            throw new GoogleMapsKeyInvalidException();
        }

        // Beyond "not empty and not absurdly long", nothing about the key is validated. Google is
        // the only authority on whether a key works, and asserting a shape here would reject keys
        // that Google itself accepts.
        var trimmed = apiKey.Trim();
        await repository.SaveAsync(
            new GoogleMapsCredentialRecord(protector.Protect(trimmed), Mask(trimmed), timeProvider.GetUtcNow()),
            cancellationToken);
    }

    public async Task<string> RevealAsync(CancellationToken cancellationToken)
    {
        if (protector is null)
        {
            throw new GoogleMapsKeyStorageUnavailableException();
        }

        var stored = await repository.GetAsync(cancellationToken)
            ?? throw new GoogleMapsKeyNotStoredException();
        return protector.Unprotect(stored.Secret);
    }

    public Task DeleteAsync(CancellationToken cancellationToken) => repository.DeleteAsync(cancellationToken);

    // Matches the client-side KeyRedactor.Mask and the mask() in gmaps.js, so the hint the API
    // stores and the redaction the log applies are the same string.
    private static string Mask(string key) =>
        key.Length < MinimumMaskableLength ? "…" : $"{key[..4]}…{key[^4..]}";
}
```

- [ ] **Step 4: Run the service tests**

Run: `dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~GoogleMapsKeyServiceTests`
Expected: PASS.

- [ ] **Step 5: Add contracts and error codes**

`src/RouteTimer.Contracts/Settings/SettingsContracts.cs`:

```csharp
namespace RouteTimer.Contracts.Settings;

public sealed record GoogleMapsKeyStatusResponse(bool Configured, string? Hint, bool StorageAvailable);

public sealed record SaveGoogleMapsKeyRequest(string ApiKey)
{
    public override string ToString() => "SaveGoogleMapsKeyRequest { ApiKey = <redacted> }";
}

public sealed record GoogleMapsKeyResponse(string ApiKey)
{
    public override string ToString() => "GoogleMapsKeyResponse { ApiKey = <redacted> }";
}
```

Add to `ErrorCodes.cs`:

```csharp
    public const string GoogleMapsKeyNotStored = "google-maps-key-not-stored";
    public const string GoogleMapsKeyInvalid = "google-maps-key-invalid";
    public const string GoogleMapsKeyStorageUnavailable = "google-maps-key-storage-unavailable";
```

- [ ] **Step 6: Add the endpoints**

`src/RouteTimer.Api/Endpoints/SettingsEndpoints.cs`:

```csharp
using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Settings;
using RouteTimer.Services.Settings;

namespace RouteTimer.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/settings/google-maps-key", GetStatusAsync);
        routes.MapPut("/api/settings/google-maps-key", SaveAsync);
        routes.MapDelete("/api/settings/google-maps-key", DeleteAsync);

        // Deliberately a POST. UseSameOriginEnforcement exempts GET, HEAD, and OPTIONS from its
        // Sec-Fetch-Site check, so a GET that returns the key would be readable by a page served
        // from any other port on this host -- exactly the case that middleware exists to close.
        routes.MapPost("/api/settings/google-maps-key/use", RevealAsync);
        return routes;
    }

    private static async Task<IResult> GetStatusAsync(
        GoogleMapsKeyService keys,
        CancellationToken cancellationToken)
    {
        var status = await keys.GetStatusAsync(cancellationToken);
        return TypedResults.Ok(new GoogleMapsKeyStatusResponse(status.Configured, status.Hint, status.StorageAvailable));
    }

    private static async Task<IResult> SaveAsync(
        SaveGoogleMapsKeyRequest request,
        GoogleMapsKeyService keys,
        CancellationToken cancellationToken)
    {
        try
        {
            await keys.SaveAsync(request.ApiKey, cancellationToken);
            return TypedResults.NoContent();
        }
        catch (GoogleMapsKeyInvalidException)
        {
            return ApiProblems.BadRequest(
                ErrorCodes.GoogleMapsKeyInvalid,
                "Enter a Google Maps API key of at most 512 characters.");
        }
        catch (GoogleMapsKeyStorageUnavailableException)
        {
            return ApiProblems.Conflict(
                ErrorCodes.GoogleMapsKeyStorageUnavailable,
                "This deployment has no Google Maps key encryption key configured, so keys cannot be saved.");
        }
    }

    private static async Task<IResult> DeleteAsync(
        GoogleMapsKeyService keys,
        CancellationToken cancellationToken)
    {
        await keys.DeleteAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RevealAsync(
        GoogleMapsKeyService keys,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(new GoogleMapsKeyResponse(await keys.RevealAsync(cancellationToken)));
        }
        catch (GoogleMapsKeyNotStoredException)
        {
            return ApiProblems.NotFound(ErrorCodes.GoogleMapsKeyNotStored, "No Google Maps API key is stored.");
        }
        catch (GoogleMapsKeyStorageUnavailableException)
        {
            return ApiProblems.Conflict(
                ErrorCodes.GoogleMapsKeyStorageUnavailable,
                "This deployment has no Google Maps key encryption key configured.");
        }
    }
}
```

- [ ] **Step 7: Register the service and the endpoints**

In `src/RouteTimer.Api/Program.cs`, near the existing `Garmin:TokenEncryptionKey` block:

```csharp
// Optional, unlike the Garmin key. Without it the rider can still type a key for one conversion;
// only saving is unavailable. A deployment that never uses Google Maps needs no new configuration.
var encodedMapsKey = builder.Configuration["GoogleMaps:KeyEncryptionKey"];
AesGcmSecretProtector? googleMapsKeyProtector = null;
if (!string.IsNullOrWhiteSpace(encodedMapsKey))
{
    var mapsKey = Convert.FromBase64String(encodedMapsKey);
    if (mapsKey.Length != 32)
    {
        throw new InvalidOperationException("GoogleMaps:KeyEncryptionKey must decode to 32 bytes.");
    }

    googleMapsKeyProtector = new AesGcmSecretProtector(mapsKey, GoogleMapsKeyService.Purpose);
}

builder.Services.AddScoped<GoogleMapsKeyService>(sp => new GoogleMapsKeyService(
    sp.GetRequiredService<IGoogleMapsCredentialRepository>(),
    googleMapsKeyProtector,
    sp.GetRequiredService<TimeProvider>()));
```

Register `IGoogleMapsCredentialRepository` alongside the other repositories, and add `app.MapSettingsEndpoints();` beside `app.MapProfileEndpoints();`.

- [ ] **Step 8: Add endpoint tests**

In `tests/RouteTimer.Api.Tests/Endpoints/SettingsEndpointsTests.cs`, assert: the status endpoint never returns the key in its body; `PUT` with an empty key returns `400` and code `google-maps-key-invalid`; `POST .../use` with nothing stored returns `404`; a saved key round-trips through `PUT` then `POST .../use`; and `DELETE` returns `204` twice in a row.

- [ ] **Step 9: Run the full suite and commit**

Run: `dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false`
Expected: PASS.

```bash
git add src/RouteTimer.Services/Settings src/RouteTimer.Contracts src/RouteTimer.Api tests
git commit -m "feat(api): save, reveal, and delete the Google Maps API key"
```

---

## Task 11: Expose the key endpoints on the typed client

**Files:**
- Modify: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`, `RouteTimerApiClient.cs`, `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`
- Test: `tests/RouteTimer.Client.Tests/Api/RouteTimerApiClientTests.cs`

- [ ] **Step 1: Write the failing client test**

Add to the existing API client test file, following its stub-handler style:

```csharp
[Fact]
public async Task Reveals_the_google_maps_key_with_a_post()
{
    var handler = new StubHandler(request =>
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/settings/google-maps-key/use", request.RequestUri!.AbsolutePath);
        return JsonResponse("""{"apiKey":"AIzaSyExampleKeyValue0123456789"}""");
    });
    var client = CreateClient(handler);

    var response = await client.UseGoogleMapsKeyAsync(CancellationToken.None);

    Assert.Equal("AIzaSyExampleKeyValue0123456789", response.ApiKey);
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RouteTimerApiClientTests`
Expected: FAIL — `UseGoogleMapsKeyAsync` does not exist.

- [ ] **Step 3: Add the four methods**

In `IRouteTimerApiClient.cs`:

```csharp
    Task<GoogleMapsKeyStatusResponse> GetGoogleMapsKeyStatusAsync(CancellationToken ct);
    Task SaveGoogleMapsKeyAsync(SaveGoogleMapsKeyRequest request, CancellationToken ct);
    Task DeleteGoogleMapsKeyAsync(CancellationToken ct);
    Task<GoogleMapsKeyResponse> UseGoogleMapsKeyAsync(CancellationToken ct);
```

In `RouteTimerApiClient.cs`:

```csharp
    public Task<GoogleMapsKeyStatusResponse> GetGoogleMapsKeyStatusAsync(CancellationToken ct) =>
        GetRequiredAsync<GoogleMapsKeyStatusResponse>("/api/settings/google-maps-key", ct);

    public Task SaveGoogleMapsKeyAsync(SaveGoogleMapsKeyRequest request, CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Put, "/api/settings/google-maps-key", request, ct);

    public Task DeleteGoogleMapsKeyAsync(CancellationToken ct) =>
        DeleteAsync("/api/settings/google-maps-key", ct);

    public Task<GoogleMapsKeyResponse> UseGoogleMapsKeyAsync(CancellationToken ct) =>
        SendJsonAsync<GoogleMapsKeyResponse>(HttpMethod.Post, "/api/settings/google-maps-key/use", new { }, ct);
```

If the file has no non-generic `SendJsonAsync` overload for a request with no response body, add one beside the existing helpers, following how `DisconnectGarminAsync` handles a bodiless response.

Add matching `On...` delegate properties and implementations to `FakeRouteTimerApiClient.cs`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~RouteTimerApiClientTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RouteTimer.Client/Api tests/RouteTimer.Client.Tests
git commit -m "feat(client): call the Google Maps key endpoints"
```

---

## Task 12: Add the Google Maps route input to the Predictions page

**Files:**
- Create: `src/RouteTimer.Client/Components/GoogleMapsRouteInput.razor`
- Modify: `src/RouteTimer.Client/Pages/Predictions.razor`, `src/RouteTimer.Client/Program.cs`, `src/RouteTimer.Client/wwwroot/css/app.css`
- Test: `tests/RouteTimer.Client.Tests/Components/GoogleMapsRouteInputTests.cs`, `tests/RouteTimer.Client.Tests/PredictionsPageTests.cs`

- [ ] **Step 1: Write the failing component tests**

```csharp
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Components;
using RouteTimer.Client.Logging;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Settings;

namespace RouteTimer.Client.Tests.Components;

public sealed class GoogleMapsRouteInputTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();

    public GoogleMapsRouteInputTests()
    {
        Services.AddSingleton<IRouteTimerApiClient>(api);
        Services.AddScoped<ActionLog>();
    }

    [Fact]
    public void Shows_the_saved_key_hint_and_offers_to_replace_it()
    {
        api.OnGetGoogleMapsKeyStatusAsync = _ =>
            Task.FromResult(new GoogleMapsKeyStatusResponse(true, "AIza…6789", true));

        var cut = Render<GoogleMapsRouteInput>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("AIza…6789", cut.Find("[data-testid=maps-key-status]").TextContent, StringComparison.Ordinal);
            cut.Find("[data-testid=maps-key-replace]");
            cut.Find("[data-testid=maps-key-delete]");
        });
    }

    [Fact]
    public void Offers_no_save_option_when_key_storage_is_unavailable()
    {
        api.OnGetGoogleMapsKeyStatusAsync = _ =>
            Task.FromResult(new GoogleMapsKeyStatusResponse(false, null, false));

        var cut = Render<GoogleMapsRouteInput>();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=maps-key-save]"));
            Assert.Contains(
                "cannot be saved",
                cut.Find("[data-testid=maps-key-status]").TextContent,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void States_plainly_what_saving_the_key_means()
    {
        api.OnGetGoogleMapsKeyStatusAsync = _ =>
            Task.FromResult(new GoogleMapsKeyStatusResponse(false, null, true));

        var cut = Render<GoogleMapsRouteInput>();

        cut.WaitForAssertion(() =>
        {
            var disclosure = cut.Find("[data-testid=maps-key-disclosure]").TextContent;
            Assert.Contains("encrypted", disclosure, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("server can decrypt", disclosure, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("anyone who can sign in", disclosure, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Blocks_conversion_when_the_url_is_empty()
    {
        api.OnGetGoogleMapsKeyStatusAsync = _ =>
            Task.FromResult(new GoogleMapsKeyStatusResponse(true, "AIza…6789", true));

        var cut = Render<GoogleMapsRouteInput>();

        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=maps-convert]").HasAttribute("disabled")));
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~GoogleMapsRouteInputTests`
Expected: FAIL — the component does not exist.

- [ ] **Step 3: Write the component**

`src/RouteTimer.Client/Components/GoogleMapsRouteInput.razor` renders, in order:

1. A key panel with `data-testid="maps-key-status"`. When `StorageAvailable` is false it says the key cannot be saved on this deployment and shows only a password input for one-off use. When `Configured` is true it shows the hint, a `maps-key-replace` button, and a `maps-key-delete` button. Otherwise it shows a password input and a `maps-key-save` button.
2. A `data-testid="maps-key-disclosure"` paragraph reading exactly: *"A saved key is encrypted at rest, but the server can decrypt it, and it is handed to this page and to Google whenever you convert a route. Anyone who can sign in to this RouteTimer can spend against it."*
3. A URL input, `data-testid="maps-url"`.
4. A travel mode `<select>`, `data-testid="maps-mode"`, options Bicycling (default), Driving, Walking, Transit, re-selected from the parsed URL when the pasted URL carries a mode.
5. A `data-testid="maps-convert"` button, disabled while busy, while the URL is empty, or while no key is available.
6. `<ActionLogView OnCopy="..." />`.

The conversion handler follows the sequence in the spec and mirrors `Home.razor` in MapToGarmin:

```csharp
private async Task ConvertAsync()
{
    if (busy) return;
    busy = true;
    log.Clear();

    var key = await ResolveKeyAsync();
    if (key is null)
    {
        busy = false;
        return;
    }

    log.UseRedactionKey(key);

    try
    {
        var url = mapUrl.Trim();
        if (GoogleMapsUrlParser.IsShortLink(url, out var code))
        {
            var resolved = await shortLinks.ResolveAsync(code, cancellation.Token);
            if (resolved is null) return;
            url = resolved;
        }

        var route = GoogleMapsUrlParser.Parse(url) with { Mode = selectedMode };
        if (route.IsSinglePoint)
        {
            log.Error("That URL describes a single place, not a route.", "Paste a /maps/dir/ route URL instead.");
            return;
        }

        if (route.Intermediates.Count > MaximumIntermediateWaypoints)
        {
            log.Error(
                $"That route has {route.Intermediates.Count} intermediate waypoints; the Directions service accepts at most {MaximumIntermediateWaypoints}.",
                "Remove intermediate stops in Google Maps and share a new link.");
            return;
        }

        log.Info($"This page's origin is {await browser.OriginAsync()}",
            "If your key has HTTP referrer restrictions, this is the value they must allow.");

        await directions.LoadApiAsync(key);
        var outcome = await directions.RouteAsync(route);

        IReadOnlyList<RoutePoint> track;
        try
        {
            track = await directions.ElevateAsync(outcome.Path);
        }
        catch (Exception exception)
        {
            log.Error("Elevation lookup failed, so this route cannot be predicted.", exception.Message);
            return;
        }

        // RouteTimer predicts from gradient. A track missing elevation would produce a confident
        // and wrong answer, so this stops rather than degrading the way MapToGarmin does.
        if (!DirectionsInterop.HasCompleteElevation(track))
        {
            log.Error(
                "Google returned no elevation for part of this route, so it cannot be predicted.",
                "Try a shorter route, or upload a GPX that already carries elevation.");
            return;
        }

        var name = RouteName(route, outcome.Waypoints);
        var gpx = RouteGpxWriter.Write(name, outcome.Waypoints, track, TimeProvider.GetUtcNow());
        var fileName = RouteGpxWriter.SuggestFileName(name);
        log.Info($"Generated {fileName} ({gpx.Length:N0} characters).");

        await OnRouteBuilt.InvokeAsync(new BuiltRoute(fileName, gpx));
    }
    catch (MapUrlParseException exception)
    {
        log.Error("Could not read that URL.", exception.Message);
    }
    catch (Exception exception)
    {
        log.Error("Conversion failed.", exception.Message);
    }
    finally
    {
        await ScrubKeyAsync();
        busy = false;
    }
}
```

`BuiltRoute` is `public sealed record BuiltRoute(string FileName, string Gpx);` in `src/RouteTimer.Client/RouteBuilder/BuiltRoute.cs`. `ResolveKeyAsync` returns the typed key when the rider entered one, otherwise calls `api.UseGoogleMapsKeyAsync`. `ScrubKeyAsync` clears the typed key field, calls `log.UseRedactionKey(null)`, and calls `directions.ScrubKeyAsync()`, exactly as MapToGarmin does.

`ParsedRoute` is a record, so `with { Mode = selectedMode }` needs `Mode` to be settable in the record's primary constructor — it already is.

- [ ] **Step 4: Wire it into the Predictions page**

In `src/RouteTimer.Client/Pages/Predictions.razor`, wrap the existing upload markup in a tab panel:

```razor
<h2>Submit a route</h2>

<div class="predictions-input-modes" role="tablist">
    <button type="button" role="tab" data-testid="predictions-mode-upload"
            class="@ModeClass(InputMode.Upload)"
            aria-selected="@(inputMode == InputMode.Upload)"
            @onclick="@(() => inputMode = InputMode.Upload)">Upload GPX</button>
    <button type="button" role="tab" data-testid="predictions-mode-maps"
            class="@ModeClass(InputMode.GoogleMaps)"
            aria-selected="@(inputMode == InputMode.GoogleMaps)"
            @onclick="@(() => inputMode = InputMode.GoogleMaps)">Google Maps route</button>
</div>

@if (inputMode == InputMode.Upload)
{
    @* the existing InputFile markup, unchanged *@
}
else
{
    <GoogleMapsRouteInput OnRouteBuilt="SubmitBuiltRouteAsync" />
}
```

Add to `@code`:

```csharp
private enum InputMode { Upload, GoogleMaps }

private InputMode inputMode = InputMode.Upload;

private string ModeClass(InputMode mode) =>
    mode == inputMode ? "predictions-mode predictions-mode--active" : "predictions-mode";

private async Task SubmitBuiltRouteAsync(BuiltRoute route)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(route.Gpx);
    var upload = new ClientFileUpload(route.FileName, bytes.Length, () => new MemoryStream(bytes));
    await SubmitUploadAsync(upload);
}
```

Extract the body of the existing `SubmitAsync` into `SubmitUploadAsync(ClientFileUpload upload)` so both modes share one submission path, and have `SubmitAsync` build its `ClientFileUpload` from `selectedFiles[0]` and call it. `CanSubmit` keeps guarding the upload mode; `SubmitBuiltRouteAsync` guards on `modelStatus?.IsReady == true` instead, because the Google Maps mode has no selected file.

Both modes keep their own state across tab switches, because neither branch's state is cleared on switch.

- [ ] **Step 5: Register the client services**

In `src/RouteTimer.Client/Program.cs`:

```csharp
builder.Services.AddScoped<ActionLog>();
builder.Services.AddScoped<DirectionsInterop>();
builder.Services.AddScoped<BrowserInterop>();
builder.Services.AddScoped<ShortLinkClient>();
```

- [ ] **Step 6: Add the page test**

Add to `tests/RouteTimer.Client.Tests/PredictionsPageTests.cs`:

```csharp
[Fact]
public void Switching_input_modes_does_not_discard_the_other_mode_state()
{
    api.OnGetModelStatusAsync = _ => Task.FromResult(ReadyModelStatus());
    api.OnGetPredictionsAsync = _ => Task.FromResult<IReadOnlyList<PredictionSummaryResponse>>([]);
    api.OnGetGoogleMapsKeyStatusAsync = _ =>
        Task.FromResult(new GoogleMapsKeyStatusResponse(true, "AIza…6789", true));

    var cut = Render<Predictions>();

    cut.WaitForAssertion(() => cut.Find("[data-testid=predictions-mode-maps]")).Click();
    cut.WaitForAssertion(() => cut.Find("[data-testid=maps-url]"));

    cut.Find("[data-testid=predictions-mode-upload]").Click();

    cut.WaitForAssertion(() => Assert.Equal(".gpx", cut.Find("input[type=file]").GetAttribute("accept")));
}
```

Use the file's existing `ReadyModelStatus()` helper, adding one modelled on `NotReadyModelStatus()` if it does not exist.

- [ ] **Step 7: Add styling**

In `src/RouteTimer.Client/wwwroot/css/app.css`, add `.predictions-input-modes`, `.predictions-mode`, and `.predictions-mode--active` rules matching the existing `.predictions-button` visual language. Port the log and form rules the `ActionLogView` needs from `~/RiderProjects/MapToGarmin/src/MapToGarmin/wwwroot/css/app.css`, prefixing any generic selector (`.inputs`, `.note`, `.actions`) with `.route-builder` so it cannot leak into other pages.

- [ ] **Step 8: Run the client tests and commit**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false`
Expected: PASS, including every pre-existing Predictions page test.

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat(client): predict from a Google Maps route on the predictions page"
```

---

## Task 13: Write the prediction GPX writer

**Files:**
- Create: `src/RouteTimer.Services/Routes/PredictionGpxWriter.cs`
- Test: `tests/RouteTimer.Services.Tests/Routes/PredictionGpxWriterTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Tests.Routes;

public sealed class PredictionGpxWriterTests
{
    private static PredictionGpxSource Source(params PersistedPredictionSegment[] segments) => new(
        "Kingston to Dorking",
        "Predicted 1:12:30 · 34.2 km · 410 m ascent · 28.3 km/h · 214 W · high confidence · model 1.4.0",
        DateTimeOffset.Parse("2026-08-26T09:00:00Z"),
        DateTimeOffset.Parse("2026-08-26T08:00:00Z"),
        segments);

    private static PersistedPredictionSegment Segment(int sequence, double lat, double lon, double ele, double cumulativeSeconds) =>
        new(sequence, lat, lon, ele, 0, 0, 0, 0, 200, 8, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(cumulativeSeconds), ConfidenceLevel.High);

    [Fact]
    public void Writes_an_untimed_course_track()
    {
        var gpx = PredictionGpxWriter.Write(Source(
            Segment(0, 51.4085000, -0.3064000, 12.4, 0),
            Segment(1, 51.4090000, -0.3070000, 15.0, 30)), timed: false);

        Assert.Contains("creator=\"RouteTimer\"", gpx, StringComparison.Ordinal);
        Assert.Contains("<name>Kingston to Dorking</name>", gpx, StringComparison.Ordinal);
        Assert.Contains("<desc>Predicted 1:12:30", gpx, StringComparison.Ordinal);
        Assert.Contains("lat=\"51.4085000\"", gpx, StringComparison.Ordinal);
        Assert.Contains("<ele>12.4</ele>", gpx, StringComparison.Ordinal);
        Assert.DoesNotContain("<trkpt", gpx.Split("<trkseg>")[0], StringComparison.Ordinal);
        Assert.DoesNotContain("<time>2026-08-26T08:00:00Z</time>", gpx, StringComparison.Ordinal);
    }

    [Fact]
    public void Writes_predicted_times_in_the_timed_variant()
    {
        var gpx = PredictionGpxWriter.Write(Source(
            Segment(0, 51.4085000, -0.3064000, 12.4, 0),
            Segment(1, 51.4090000, -0.3070000, 15.0, 90)), timed: true);

        Assert.Contains("<time>2026-08-26T08:00:00Z</time>", gpx, StringComparison.Ordinal);
        Assert.Contains("<time>2026-08-26T08:01:30Z</time>", gpx, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_prediction_with_no_segments()
    {
        Assert.Throws<PredictionNotCompleteException>(() => PredictionGpxWriter.Write(Source(), timed: false));
    }

    [Fact]
    public void Writes_no_byte_order_mark()
    {
        var gpx = PredictionGpxWriter.Write(Source(
            Segment(0, 51.4085000, -0.3064000, 12.4, 0),
            Segment(1, 51.4090000, -0.3070000, 15.0, 30)), timed: false);

        Assert.False(gpx.StartsWith('﻿'));
    }

    [Fact]
    public void Slugifies_the_file_name()
    {
        Assert.Equal("Kingston-to-Dorking.gpx", PredictionGpxWriter.SuggestFileName("Kingston to Dorking"));
        Assert.Equal("route.gpx", PredictionGpxWriter.SuggestFileName("///"));
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~PredictionGpxWriterTests`
Expected: FAIL — the type does not exist.

- [ ] **Step 3: Write the writer**

`src/RouteTimer.Services/Routes/PredictionGpxWriter.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Routes;

public sealed class PredictionNotCompleteException()
    : Exception("The prediction has no route segments, so it cannot be exported.");

public sealed record PredictionGpxSource(
    string RouteName,
    string Description,
    DateTimeOffset GeneratedAt,
    DateTimeOffset StartAt,
    IReadOnlyList<PersistedPredictionSegment> Segments);

public static partial class PredictionGpxWriter
{
    private const string Namespace = "http://www.topografix.com/GPX/1/1";
    private const int MaximumFileNameStemLength = 80;

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonFileNameCharacters();

    public static string Write(PredictionGpxSource source, bool timed)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Segments.Count == 0)
        {
            throw new PredictionNotCompleteException();
        }

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            // A StringWriter would force a utf-16 declaration, so the document is built in a
            // MemoryStream. UTF8Encoding(false) suppresses the byte order mark, which some GPX
            // consumers choke on.
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("gpx", Namespace);
            writer.WriteAttributeString("version", "1.1");
            writer.WriteAttributeString("creator", "RouteTimer");

            writer.WriteStartElement("metadata", Namespace);
            writer.WriteElementString("name", Namespace, source.RouteName);
            writer.WriteElementString("desc", Namespace, source.Description);
            writer.WriteElementString("time", Namespace, Instant(source.GeneratedAt));
            writer.WriteEndElement();

            writer.WriteStartElement("trk", Namespace);
            writer.WriteElementString("name", Namespace, source.RouteName);
            writer.WriteStartElement("trkseg", Namespace);

            foreach (var segment in source.Segments.OrderBy(segment => segment.Sequence))
            {
                writer.WriteStartElement("trkpt", Namespace);
                writer.WriteAttributeString("lat", segment.Latitude.ToString("F7", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("lon", segment.Longitude.ToString("F7", CultureInfo.InvariantCulture));
                writer.WriteElementString("ele", Namespace, segment.ElevationMetres.ToString("F1", CultureInfo.InvariantCulture));

                // Times are opt-in: several course importers treat a timestamped track as an
                // activity rather than a route, so the variant Garmin receives carries none.
                if (timed)
                {
                    writer.WriteElementString("time", Namespace, Instant(source.StartAt + segment.CumulativeMovingTime));
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return new UTF8Encoding(false).GetString(stream.ToArray());
    }

    public static string SuggestFileName(string routeName)
    {
        var cleaned = NonFileNameCharacters().Replace(routeName, "-").Trim('-');
        if (cleaned.Length > MaximumFileNameStemLength)
        {
            cleaned = cleaned[..MaximumFileNameStemLength].TrimEnd('-');
        }

        return cleaned.Length == 0 ? "route.gpx" : $"{cleaned}.gpx";
    }

    private static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~PredictionGpxWriterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RouteTimer.Services/Routes/PredictionGpxWriter.cs tests/RouteTimer.Services.Tests/Routes
git commit -m "feat(services): write prediction GPX in timed and untimed variants"
```

---

## Task 14: Serve the prediction GPX

**Files:**
- Modify: `src/RouteTimer.Services/Persistence/IPredictionRepository.cs`, `src/RouteTimer.Persistence/Repositories/PredictionRepository.cs`, `src/RouteTimer.Services/Predictions/PredictionQueryService.cs`, `src/RouteTimer.Api/Endpoints/PredictionsEndpoints.cs`, `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`
- Test: `tests/RouteTimer.Persistence.Tests/PredictionRepositoryTests.cs`, `tests/RouteTimer.Api.Tests/Endpoints/PredictionEndpointsTests.cs`

- [ ] **Step 1: Write the failing repository test**

Add to the existing prediction repository tests:

```csharp
[Fact]
public async Task Reads_a_gpx_source_carrying_the_upload_name_and_segments()
{
    var repository = CreateRepository();
    var predictionId = await SeedPublishedPredictionAsync(repository, uploadFileName: "kingston-dorking.gpx");

    var source = await repository.GetGpxSourceAsync(predictionId, CancellationToken.None);

    Assert.NotNull(source);
    Assert.Equal("kingston-dorking", source.RouteName);
    Assert.NotEmpty(source.Segments);
    Assert.Contains("Predicted", source.Description, StringComparison.Ordinal);
}

[Fact]
public async Task Returns_null_for_an_unknown_prediction()
{
    var repository = CreateRepository();

    Assert.Null(await repository.GetGpxSourceAsync(Guid.NewGuid(), CancellationToken.None));
}
```

Use the test file's existing seeding helper, extending it with an `uploadFileName` parameter if it does not take one.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/RouteTimer.Persistence.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~PredictionRepositoryTests`
Expected: FAIL — `GetGpxSourceAsync` does not exist.

- [ ] **Step 3: Add the repository read**

In `IPredictionRepository`:

```csharp
    Task<PredictionGpxSource?> GetGpxSourceAsync(Guid predictionId, CancellationToken cancellationToken);
```

Add `using RouteTimer.Services.Routes;` to that file. In `PredictionRepository`:

```csharp
public async Task<PredictionGpxSource?> GetGpxSourceAsync(Guid predictionId, CancellationToken cancellationToken)
{
    var prediction = await context.Predictions
        .AsNoTracking()
        .Include(entity => entity.Upload)
        .Include(entity => entity.Segments)
        .SingleOrDefaultAsync(entity => entity.Id == predictionId, cancellationToken);

    if (prediction is null)
    {
        return null;
    }

    var name = Path.GetFileNameWithoutExtension(prediction.Upload?.FileName ?? string.Empty);
    if (string.IsNullOrWhiteSpace(name))
    {
        name = $"RouteTimer prediction {prediction.Id}";
    }

    return new PredictionGpxSource(
        name,
        Describe(prediction),
        DateTimeOffset.UtcNow,
        prediction.CompletedAt ?? prediction.CreatedAt,
        prediction.Segments
            .OrderBy(segment => segment.Sequence)
            .Select(ToPersistedSegment)
            .ToList());
}

private static string Describe(PredictionEntity prediction)
{
    var parts = new List<string>();
    if (prediction.MovingSeconds is { } seconds)
    {
        parts.Add($"Predicted {TimeSpan.FromSeconds(seconds):h\\:mm\\:ss}");
    }

    if (prediction.DistanceMetres is { } distance)
    {
        parts.Add($"{distance / 1000:F1} km");
    }

    if (prediction.AscentMetres is { } ascent)
    {
        parts.Add($"{ascent:F0} m ascent");
    }

    if (prediction.AverageSpeedMetresPerSecond is { } speed)
    {
        parts.Add($"{speed * 3.6:F1} km/h");
    }

    if (prediction.AveragePowerWatts is { } power)
    {
        parts.Add($"{power:F0} W");
    }

    if (!string.IsNullOrWhiteSpace(prediction.Confidence))
    {
        parts.Add($"{prediction.Confidence.ToLowerInvariant()} confidence");
    }

    parts.Add($"model {prediction.ModelVersion}");
    return string.Join(" · ", parts);
}
```

`ToPersistedSegment` already exists in this repository for the detail read; reuse it rather than writing a second mapping. If it is currently a local function, promote it to a `private static` method.

`DateTimeOffset.UtcNow` here is deliberate and matches the surrounding repository code; the writer's testable instants are the ones passed in by its tests.

- [ ] **Step 4: Add the query service pass-through and the endpoint**

In `PredictionQueryService`:

```csharp
public Task<PredictionGpxSource?> GetGpxSourceAsync(Guid predictionId, CancellationToken cancellationToken) =>
    repository.GetGpxSourceAsync(predictionId, cancellationToken);
```

Add to `ErrorCodes.cs`:

```csharp
    public const string PredictionNotComplete = "prediction-not-complete";
```

In `PredictionEndpoints.MapPredictionEndpoints`:

```csharp
        routes.MapGet("/api/predictions/{id:guid}/gpx", GetPredictionGpxAsync);
```

And the handler:

```csharp
    private static async Task<IResult> GetPredictionGpxAsync(
        Guid id,
        bool? timed,
        PredictionQueryService predictions,
        CancellationToken cancellationToken)
    {
        var source = await predictions.GetGpxSourceAsync(id, cancellationToken);
        if (source is null)
        {
            return ApiProblems.NotFound(ErrorCodes.PredictionNotFound, "The prediction was not found.");
        }

        try
        {
            var gpx = PredictionGpxWriter.Write(source, timed ?? false);
            return TypedResults.File(
                System.Text.Encoding.UTF8.GetBytes(gpx),
                "application/gpx+xml",
                PredictionGpxWriter.SuggestFileName(source.RouteName));
        }
        catch (PredictionNotCompleteException)
        {
            return ApiProblems.Conflict(
                ErrorCodes.PredictionNotComplete,
                "This prediction has not produced a route yet, so it cannot be exported.");
        }
    }
```

`TypedResults.File` with a file name sets `Content-Disposition: attachment` on its own.

- [ ] **Step 5: Add the endpoint tests**

In `tests/RouteTimer.Api.Tests/Endpoints/PredictionEndpointsTests.cs`, assert: a completed prediction returns `200`, `application/gpx+xml`, a `Content-Disposition` naming a `.gpx` file, and a body containing `<trkpt`; `?timed=true` adds `<time>`; an unknown id returns `404`; and a queued prediction returns `409` with `prediction-not-complete`.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false`
Expected: PASS.

```bash
git add src tests
git commit -m "feat(api): download a completed prediction as GPX"
```

---

## Task 15: Offer the GPX downloads in the UI

**Files:**
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`, `src/RouteTimer.Client/Pages/Predictions.razor`
- Test: `tests/RouteTimer.Client.Tests/PredictionDetailPageTests.cs`, `tests/RouteTimer.Client.Tests/PredictionsPageTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `PredictionDetailPageTests.cs`:

```csharp
[Fact]
public void Offers_both_gpx_variants_for_a_completed_prediction()
{
    var predictionId = Guid.NewGuid();
    api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(CompletedDetail(predictionId));

    var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

    cut.WaitForAssertion(() =>
    {
        Assert.Equal(
            $"/api/predictions/{predictionId}/gpx",
            cut.Find("[data-testid=prediction-download-gpx]").GetAttribute("href"));
        Assert.Equal(
            $"/api/predictions/{predictionId}/gpx?timed=true",
            cut.Find("[data-testid=prediction-download-gpx-timed]").GetAttribute("href"));
    });
}
```

Add to `PredictionsPageTests.cs`:

```csharp
[Fact]
public void History_rows_link_to_the_untimed_gpx()
{
    var predictionId = Guid.NewGuid();
    api.OnGetModelStatusAsync = _ => Task.FromResult(ReadyModelStatus());
    api.OnGetPredictionsAsync = _ => Task.FromResult<IReadOnlyList<PredictionSummaryResponse>>([CompletedSummary(predictionId)]);

    var cut = Render<Predictions>();

    cut.WaitForAssertion(() => Assert.Equal(
        $"/api/predictions/{predictionId}/gpx",
        cut.Find($"[data-testid=prediction-download-gpx-{predictionId}]").GetAttribute("href")));
}
```

Use each file's existing helpers for a completed detail and summary, adding one if absent.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter "FullyQualifiedName~PredictionDetailPageTests|FullyQualifiedName~PredictionsPageTests"`
Expected: FAIL — the elements do not exist.

- [ ] **Step 3: Add the links**

In `PredictionDetail.razor`, inside the summary actions, rendered only when the prediction has segments:

```razor
<a data-testid="prediction-download-gpx"
   class="predictions-button predictions-button--secondary"
   href="@($"/api/predictions/{Id}/gpx")">Download GPX</a>
<a data-testid="prediction-download-gpx-timed"
   class="predictions-button predictions-button--secondary"
   href="@($"/api/predictions/{Id}/gpx?timed=true")">Download GPX with predicted times</a>
```

In `Predictions.razor`, inside `predictions-history-item__actions`, rendered only when `prediction.MovingSeconds is not null`:

```razor
<a data-testid="@($"prediction-download-gpx-{prediction.Id}")"
   class="predictions-button predictions-button--secondary"
   href="@($"/api/predictions/{prediction.Id}/gpx")">GPX</a>
```

A plain anchor, not a JavaScript download: the browser streams the response straight from the API, so a large route never passes through WebAssembly memory.

- [ ] **Step 4: Run the tests and commit**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false`
Expected: PASS.

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat(client): download predictions as GPX"
```

---

## Task 16: Build the Garmin course payload in the adapter

**Gate:** do not start until Task 1 recorded a successful spike.

**Files:**
- Create: `garmin-adapter/src/routetimer_garmin/courses.py`
- Test: `garmin-adapter/tests/test_courses.py`

- [ ] **Step 1: Write the failing tests**

```python
import math

import pytest

from routetimer_garmin.courses import (
    ACTIVITY_TYPE_IDS,
    CoursePayloadError,
    build_course_payload,
    initial_bearing,
    haversine_metres,
)


def _points():
    return [
        {"latitude": 51.4085, "longitude": -0.3064, "elevation": 12.4},
        {"latitude": 51.4090, "longitude": -0.3070, "elevation": 15.0},
        {"latitude": 51.4100, "longitude": -0.3080, "elevation": 30.0},
    ]


def test_haversine_matches_a_known_separation():
    north = {"latitude": 51.0, "longitude": 0.0}
    south = {"latitude": 50.0, "longitude": 0.0}
    assert haversine_metres(north, south) == pytest.approx(111_195, rel=0.01)


def test_initial_bearing_due_east_is_ninety_degrees():
    west = {"latitude": 0.0, "longitude": 0.0}
    east = {"latitude": 0.0, "longitude": 1.0}
    assert initial_bearing(west, east) == pytest.approx(90.0, abs=0.1)


def test_payload_carries_cumulative_distance_and_a_total():
    payload = build_course_payload(
        {"geoPoints": _points()},
        course_name="Kingston to Dorking",
        activity_type_id=ACTIVITY_TYPE_IDS["road_biking"],
        description=None,
        elevation_gain_metres=17.6,
        elevation_loss_metres=0.0,
    )

    points = payload["geoPoints"]
    assert points[0]["distance"] == 0.0
    assert points[1]["distance"] > 0
    assert points[2]["distance"] > points[1]["distance"]
    assert payload["distanceMeter"] == pytest.approx(points[2]["distance"])
    assert payload["courseLines"][0]["numberOfPoints"] == 3
    assert payload["courseLines"][0]["distanceInMeters"] == payload["distanceMeter"]


def test_payload_sends_our_own_elevation_totals():
    payload = build_course_payload(
        {"geoPoints": _points()},
        course_name="Kingston to Dorking",
        activity_type_id=10,
        description=None,
        elevation_gain_metres=17.6,
        elevation_loss_metres=3.2,
    )

    assert payload["elevationGainMeter"] == 17.6
    assert payload["elevationLossMeter"] == 3.2


def test_payload_bounding_box_spans_the_points():
    payload = build_course_payload(
        {"geoPoints": _points()},
        course_name="R",
        activity_type_id=10,
        description=None,
        elevation_gain_metres=0.0,
        elevation_loss_metres=0.0,
    )

    box = payload["boundingBox"]
    assert box["lowerLeft"]["latitude"] == 51.4085
    assert box["upperRight"]["latitude"] == 51.4100
    assert box["center"]["latitude"] == pytest.approx((51.4085 + 51.4100) / 2)


def test_payload_defaults_missing_elevation_to_zero():
    payload = build_course_payload(
        {"geoPoints": [{"latitude": 51.0, "longitude": 0.0}, {"latitude": 51.1, "longitude": 0.1}]},
        course_name="R",
        activity_type_id=10,
        description=None,
        elevation_gain_metres=0.0,
        elevation_loss_metres=0.0,
    )

    assert all(point["elevation"] == 0.0 for point in payload["geoPoints"])


@pytest.mark.parametrize("parsed", [{"geoPoints": []}, {"geoPoints": [{"latitude": 1.0, "longitude": 1.0}]}, {}])
def test_rejects_fewer_than_two_points(parsed):
    with pytest.raises(CoursePayloadError):
        build_course_payload(
            parsed,
            course_name="R",
            activity_type_id=10,
            description=None,
            elevation_gain_metres=0.0,
            elevation_loss_metres=0.0,
        )
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd garmin-adapter && python -m pytest tests/test_courses.py -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'routetimer_garmin.courses'`.

- [ ] **Step 3: Write the module**

`garmin-adapter/src/routetimer_garmin/courses.py`:

```python
"""Course payload construction for Garmin Connect.

Garmin's course import is a two-step, undocumented flow. POST /course-service/course/import
parses an uploaded GPX and returns geoPoints but no distance, bounding box, or start point;
POST /course-service/course saves the enriched payload. This module owns the arithmetic
between the two steps and nothing else, so it stays testable without a Garmin session.
"""

from __future__ import annotations

import math
from typing import Any, Final

EARTH_RADIUS_M: Final = 6_371_000.0

ACTIVITY_TYPE_IDS: Final[dict[str, int]] = {
    "cycling": 2,
    "gravel_cycling": 4,
    "mountain_biking": 5,
    "road_biking": 10,
}

DEFAULT_ACTIVITY_TYPE: Final = "road_biking"


class CoursePayloadError(Exception):
    """The parsed course cannot be turned into a valid create-course payload."""


def haversine_metres(first: dict[str, float], second: dict[str, float]) -> float:
    lat1, lon1 = math.radians(first["latitude"]), math.radians(first["longitude"])
    lat2, lon2 = math.radians(second["latitude"]), math.radians(second["longitude"])
    dlat, dlon = lat2 - lat1, lon2 - lon1
    a = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
    return 2 * EARTH_RADIUS_M * math.asin(math.sqrt(a))


def initial_bearing(first: dict[str, float], second: dict[str, float]) -> float:
    lat1, lat2 = math.radians(first["latitude"]), math.radians(second["latitude"])
    dlon = math.radians(second["longitude"] - first["longitude"])
    x = math.sin(dlon) * math.cos(lat2)
    y = math.cos(lat1) * math.sin(lat2) - math.sin(lat1) * math.cos(lat2) * math.cos(dlon)
    return (math.degrees(math.atan2(x, y)) + 360) % 360


def build_course_payload(
    parsed: dict[str, Any],
    *,
    course_name: str,
    activity_type_id: int,
    description: str | None,
    elevation_gain_metres: float,
    elevation_loss_metres: float,
) -> dict[str, Any]:
    points = list(parsed.get("geoPoints") or [])
    if len(points) < 2:
        raise CoursePayloadError("The parsed course has fewer than two geo points.")

    total = 0.0
    for index, point in enumerate(points):
        if index == 0:
            point["distance"] = 0.0
        else:
            total += haversine_metres(points[index - 1], point)
            point["distance"] = total
        if point.get("elevation") is None:
            point["elevation"] = 0.0

    lats = [point["latitude"] for point in points]
    lons = [point["longitude"] for point in points]

    return {
        "courseName": course_name,
        "description": description,
        "openStreetMap": False,
        "matchedToSegments": False,
        "userProfilePk": None,
        "userGroupPk": None,
        "rulePK": 2,  # private
        "geoRoutePk": None,
        "sourceTypeId": 3,  # GPX
        "sourcePk": None,
        "distanceMeter": total,
        # RouteTimer knows the real elevation profile -- from Google's Elevation service or the
        # rider's own GPX -- so it sends its own totals instead of the zeros that leave Garmin to
        # backfill from its terrain database. Garmin may still override them.
        "elevationGainMeter": elevation_gain_metres,
        "elevationLossMeter": elevation_loss_metres,
        "startPoint": {
            "latitude": points[0]["latitude"],
            "longitude": points[0]["longitude"],
            "elevation": points[0]["elevation"],
            "distance": None,
            "timestamp": None,
        },
        "coursePoints": [],
        "boundingBox": {
            "center": {
                "latitude": (min(lats) + max(lats)) / 2,
                "longitude": (min(lons) + max(lons)) / 2,
            },
            "lowerLeft": {"latitude": min(lats), "longitude": min(lons)},
            "upperRight": {"latitude": max(lats), "longitude": max(lons)},
            "lowerLeftLatIsSet": True,
            "lowerLeftLongIsSet": True,
            "upperRightLatIsSet": True,
            "upperRightLongIsSet": True,
        },
        "hasShareableEvent": False,
        "hasTurnDetectionDisabled": False,
        "activityTypePk": activity_type_id,
        "virtualPartnerId": None,
        "includeLaps": False,
        "elapsedSeconds": None,
        "speedMeterPerSecond": None,
        "courseLines": [
            {
                "courseId": None,
                "sortOrder": 1,
                "numberOfPoints": len(points),
                "distanceInMeters": total,
                "bearing": initial_bearing(points[0], points[-1]),
                "points": points,
                "coordinateSystem": "WGS84",
                "originalCoordinateSystem": "WGS84",
            }
        ],
        "coordinateSystem": "WGS84",
        "targetCoordinateSystem": "WGS84",
        "originalCoordinateSystem": "WGS84",
        "consumer": None,
        "elevationSource": 3,
        "hasPaceBand": False,
        "hasPowerGuide": False,
        "favorite": False,
        "startNote": None,
        "finishNote": None,
        "cutoffDuration": None,
        "geoPoints": points,
    }
```

- [ ] **Step 4: Run the tests**

Run: `cd garmin-adapter && python -m pytest tests/test_courses.py -q`
Expected: PASS.

- [ ] **Step 5: Check types and lint**

Run: `cd garmin-adapter && python -m mypy && python -m ruff check .`
Expected: no errors.

- [ ] **Step 6: Commit**

```bash
git add garmin-adapter/src/routetimer_garmin/courses.py garmin-adapter/tests/test_courses.py
git commit -m "feat(adapter): build the Garmin create-course payload"
```

---

## Task 17: Add the adapter's course endpoint

**Files:**
- Modify: `garmin-adapter/src/routetimer_garmin/facade.py`, `service.py`, `api.py`, `errors.py`
- Test: `garmin-adapter/tests/test_courses.py` (extend), plus the existing api test module

- [ ] **Step 1: Write the failing service and endpoint tests**

Add to `garmin-adapter/tests/test_courses.py`:

```python
from routetimer_garmin.errors import AdapterError


class _FakeGarminClient:
    def __init__(self, parsed, saved):
        self._parsed = parsed
        self._saved = saved
        self.calls = []

    def post(self, service, path, **kwargs):
        self.calls.append((path, kwargs))
        if path == "/course-service/course/import":
            return self._parsed
        if path == "/course-service/course":
            return self._saved
        raise AssertionError(f"unexpected path {path}")


def test_create_course_posts_the_gpx_then_saves(monkeypatch):
    from routetimer_garmin.facade import TokenSession

    client = _FakeGarminClient(
        parsed={"courseName": "Parsed name", "geoPoints": _points()},
        saved={"courseId": 4242, "courseName": "Kingston to Dorking", "distanceMeter": 1234.5},
    )
    session = TokenSession(_garmin_double(client))

    result = session.create_course(
        gpx=b"<gpx/>",
        file_name="route.gpx",
        course_name="Kingston to Dorking",
        activity_type="road_biking",
        description=None,
        elevation_gain_metres=17.6,
        elevation_loss_metres=3.2,
    )

    assert result.course_id == 4242
    assert [call[0] for call in client.calls] == [
        "/course-service/course/import",
        "/course-service/course",
    ]
    assert client.calls[0][1]["files"]["file"][2] == "application/gpx+xml"
    assert client.calls[1][1]["json"]["elevationGainMeter"] == 17.6


def test_create_course_rejects_an_unknown_activity_type():
    from routetimer_garmin.facade import TokenSession

    session = TokenSession(_garmin_double(_FakeGarminClient({"geoPoints": _points()}, {"courseId": 1})))

    with pytest.raises(AdapterError) as raised:
        session.create_course(
            gpx=b"<gpx/>",
            file_name="route.gpx",
            course_name="R",
            activity_type="unicycling",
            description=None,
            elevation_gain_metres=0.0,
            elevation_loss_metres=0.0,
        )

    assert raised.value.code == "request-invalid"
```

Write `_garmin_double(client)` as a small object exposing `.client` returning the fake, matching how the existing adapter tests double `Garmin`.

- [ ] **Step 2: Run them and watch them fail**

Run: `cd garmin-adapter && python -m pytest tests/test_courses.py -q`
Expected: FAIL — `TokenSession` has no attribute `create_course`.

- [ ] **Step 3: Add the facade method**

In `facade.py`, add a result dataclass and the method on `TokenSession`:

```python
@dataclass(frozen=True, slots=True)
class CreatedCourse:
    course_id: int
    course_name: str
    distance_metres: float | None
    elevation_gain_metres: float | None
    elevation_loss_metres: float | None
```

```python
    def create_course(
        self,
        *,
        gpx: bytes,
        file_name: str,
        course_name: str,
        activity_type: str,
        description: str | None,
        elevation_gain_metres: float,
        elevation_loss_metres: float,
    ) -> CreatedCourse:
        activity_type_id = ACTIVITY_TYPE_IDS.get(activity_type.lower())
        if activity_type_id is None:
            raise AdapterError("request-invalid", 400)

        try:
            parsed = self._garmin.client.post(
                "connectapi",
                "/course-service/course/import",
                files={"file": (file_name, gpx, "application/gpx+xml")},
                api=True,
            )
        except Exception as error:
            raise _translate_error(error, "course-rejected") from None

        if not isinstance(parsed, dict):
            raise AdapterError("response-invalid", 502)

        try:
            payload = build_course_payload(
                parsed,
                course_name=course_name or parsed.get("courseName") or file_name,
                activity_type_id=activity_type_id,
                description=description,
                elevation_gain_metres=elevation_gain_metres,
                elevation_loss_metres=elevation_loss_metres,
            )
        except CoursePayloadError:
            raise AdapterError("course-rejected", 422) from None

        try:
            saved = self._garmin.client.post("connectapi", "/course-service/course", json=payload, api=True)
        except Exception as error:
            raise _translate_error(error, "course-rejected") from None

        course_id = (saved or {}).get("courseId")
        if not isinstance(course_id, int):
            raise AdapterError("response-invalid", 502)

        return CreatedCourse(
            course_id=course_id,
            course_name=saved.get("courseName") or course_name,
            distance_metres=saved.get("distanceMeter"),
            elevation_gain_metres=saved.get("elevationGainMeter"),
            elevation_loss_metres=saved.get("elevationLossMeter"),
        )
```

Import `ACTIVITY_TYPE_IDS`, `CoursePayloadError`, and `build_course_payload` from `routetimer_garmin.courses`. Add `"course-rejected"` to whatever enumeration `errors.py` uses to validate codes, if it validates them.

- [ ] **Step 4: Add the service and endpoint layers**

In `service.py`, add a `CourseResult` dataclass carrying the `CreatedCourse` plus the refreshed `token_json`, and a `create_course` method on `GarminService` that opens a token session, calls `create_course`, and dumps the refreshed tokens — following exactly how `download_fit` on the same class is written.

In `api.py`, add:

```python
class CourseRequest(TokenRequest):
    file_name: str = Field(alias="fileName")
    course_name: str = Field(alias="courseName")
    activity_type: str = Field(alias="activityType", default="road_biking")
    description: str | None = None
    elevation_gain_metres: float = Field(alias="elevationGainMetres", default=0.0)
    elevation_loss_metres: float = Field(alias="elevationLossMetres", default=0.0)


class CourseResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    course_id: int = Field(alias="courseId")
    course_name: str = Field(alias="courseName")
    token_json: str = Field(alias="tokenJson", repr=False)


@app.post("/v1/courses", response_model=CourseResponse)
async def create_course(
    payload: Annotated[str, Form()],
    file: Annotated[UploadFile, File()],
) -> CourseResponse:
    request = CourseRequest.model_validate_json(payload)
    gpx = await file.read(MAX_FILE_BYTES + 1)
    if len(gpx) > MAX_FILE_BYTES:
        raise AdapterError("request-invalid", 400)

    result = _service.create_course(
        token_json=request.token.get_secret_value(),
        gpx=gpx,
        file_name=request.file_name,
        course_name=request.course_name,
        activity_type=request.activity_type,
        description=request.description,
        elevation_gain_metres=request.elevation_gain_metres,
        elevation_loss_metres=request.elevation_loss_metres,
    )
    return CourseResponse(
        courseId=result.course.course_id,
        courseName=result.course.course_name,
        tokenJson=result.token_json,
    )
```

Add `File`, `Form`, and `UploadFile` to the `fastapi` import. Rename the existing `MAX_FIT_BYTES` constant in `service.py` to `MAX_FILE_BYTES`, updating its FIT call sites, so one 50 MB cap covers both directions. Never log the GPX, the token, or the payload — `CourseRequest` inherits `_SecretRequest`, which already suppresses its repr.

- [ ] **Step 5: Run the adapter suite**

Run: `cd garmin-adapter && python -m pytest -q && python -m mypy && python -m ruff check .`
Expected: PASS with no type or lint errors.

- [ ] **Step 6: Commit**

```bash
git add garmin-adapter
git commit -m "feat(adapter): create a Garmin course from GPX"
```

---

## Task 18: Call the course endpoint from the .NET adapter client

**Files:**
- Modify: `src/RouteTimer.Services/Garmin/GarminAdapterContracts.cs`, `src/RouteTimer.Api/Garmin/GarminAdapterClient.cs`
- Test: `tests/RouteTimer.Api.Tests/Garmin/GarminAdapterClientTests.cs`

- [ ] **Step 1: Write the failing test**

Add to the existing adapter client tests, following their stub-handler style:

```csharp
[Fact]
public async Task Creates_a_course_and_returns_the_refreshed_token()
{
    var handler = new StubHandler(request =>
    {
        Assert.Equal("/v1/courses", request.RequestUri!.AbsolutePath);
        return JsonResponse("""{"courseId":4242,"courseName":"Kingston to Dorking","tokenJson":"refreshed"}""");
    });
    var client = new GarminAdapterClient(new HttpClient(handler) { BaseAddress = new Uri("http://adapter") });

    var created = await client.CreateCourseAsync(
        "token",
        new GarminCourseRequest("route.gpx", "Kingston to Dorking", "road_biking", null, 410, 405, "<gpx/>"u8.ToArray()),
        CancellationToken.None);

    Assert.Equal(4242, created.CourseId);
    Assert.Equal("refreshed", created.TokenJson);
}

[Fact]
public async Task Translates_a_rejected_course_into_a_typed_error()
{
    var handler = new StubHandler(_ => ProblemResponse(HttpStatusCode.UnprocessableEntity, "course-rejected"));
    var client = new GarminAdapterClient(new HttpClient(handler) { BaseAddress = new Uri("http://adapter") });

    var exception = await Assert.ThrowsAsync<GarminAdapterException>(() => client.CreateCourseAsync(
        "token",
        new GarminCourseRequest("route.gpx", "R", "road_biking", null, 0, 0, "<gpx/>"u8.ToArray()),
        CancellationToken.None));

    Assert.Equal(GarminAdapterError.CourseRejected, exception.Error);
}
```

Reuse the file's existing `JsonResponse` and error-response helpers; add a `ProblemResponse` helper if the file names it differently.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/RouteTimer.Api.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~GarminAdapterClientTests`
Expected: FAIL — `CreateCourseAsync` does not exist.

- [ ] **Step 3: Extend the contracts**

In `GarminAdapterContracts.cs`:

```csharp
public sealed record GarminCourseRequest(
    string FileName,
    string CourseName,
    string ActivityType,
    string? Description,
    double ElevationGainMetres,
    double ElevationLossMetres,
    byte[] Gpx);

public sealed record GarminAdapterCourse(long CourseId, string CourseName, string TokenJson);
```

Add `CourseRejected` to `GarminAdapterError`, and to `IGarminAdapterClient`:

```csharp
    Task<GarminAdapterCourse> CreateCourseAsync(string tokenJson, GarminCourseRequest request, CancellationToken cancellationToken);
```

- [ ] **Step 4: Implement it**

In `GarminAdapterClient.cs`, send a multipart request with a `payload` JSON part and a `file` part, mirroring how `DownloadFitAsync` handles streams and how `SendJsonAsync` validates responses. Map the adapter's `course-rejected` code to `GarminAdapterError.CourseRejected` wherever the existing code-to-error mapping lives, and validate that `courseId` is positive and `tokenJson` is non-empty before returning, throwing `ResponseInvalid()` otherwise.

- [ ] **Step 5: Run the tests and commit**

Run: `dotnet test tests/RouteTimer.Api.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~GarminAdapterClientTests`
Expected: PASS.

```bash
git add src/RouteTimer.Services/Garmin src/RouteTimer.Api/Garmin tests/RouteTimer.Api.Tests/Garmin
git commit -m "feat(api): call the adapter's course endpoint"
```

---

## Task 19: Orchestrate and record the course push

**Files:**
- Create: `src/RouteTimer.Services/Garmin/GarminCourseService.cs`
- Modify: `src/RouteTimer.Persistence/Entities/PredictionEntity.cs`, `src/RouteTimer.Persistence/RouteTimerDbContext.cs`, `src/RouteTimer.Persistence/Repositories/PredictionRepository.cs`, `src/RouteTimer.Services/Persistence/IPredictionRepository.cs`, `src/RouteTimer.Contracts/Predictions/PredictionContracts.cs`, `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`, `src/RouteTimer.Api/Endpoints/PredictionsEndpoints.cs`, `src/RouteTimer.Api/Program.cs`
- Test: `tests/RouteTimer.Services.Tests/Garmin/GarminCourseServiceTests.cs`, `tests/RouteTimer.Api.Tests/Endpoints/PredictionEndpointsTests.cs`

- [ ] **Step 1: Write the failing service tests**

```csharp
using RouteTimer.Services.Garmin;

namespace RouteTimer.Services.Tests.Garmin;

public sealed class GarminCourseServiceTests
{
    [Fact]
    public async Task Requires_a_connected_garmin_account()
    {
        var service = CreateService(connection: null);

        await Assert.ThrowsAsync<GarminConnectionRequiredException>(
            () => service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Refuses_a_prediction_that_has_no_route()
    {
        var service = CreateService(connection: Connected(), segments: []);

        await Assert.ThrowsAsync<PredictionNotCompleteException>(
            () => service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Sends_the_untimed_variant_and_records_the_course_id()
    {
        var adapter = new FakeGarminAdapterClient
        {
            OnCreateCourseAsync = (_, request, _) =>
            {
                var gpx = System.Text.Encoding.UTF8.GetString(request.Gpx);
                Assert.DoesNotContain("<time>2026", gpx, StringComparison.Ordinal);
                return Task.FromResult(new GarminAdapterCourse(4242, "Kingston to Dorking", "refreshed-token"));
            }
        };
        var repository = new FakePredictionRepository(WithSegments());
        var service = CreateService(connection: Connected(), adapter: adapter, predictions: repository);

        var created = await service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None);

        Assert.Equal(4242, created.CourseId);
        Assert.Equal(4242, repository.RecordedCourseId);
    }

    [Fact]
    public async Task Persists_the_refreshed_token()
    {
        var connections = new FakeGarminConnectionRepository(Connected());
        var service = CreateService(connection: Connected(), connections: connections);

        await service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None);

        Assert.Equal("refreshed-token", connections.SavedPlaintextToken);
    }

    [Fact]
    public async Task Defaults_the_activity_type_to_road_cycling()
    {
        var adapter = new FakeGarminAdapterClient
        {
            OnCreateCourseAsync = (_, request, _) =>
            {
                Assert.Equal("road_biking", request.ActivityType);
                return Task.FromResult(new GarminAdapterCourse(1, "R", "refreshed-token"));
            }
        };
        var service = CreateService(connection: Connected(), adapter: adapter);

        await service.CreateCourseAsync(PredictionId, new GarminCourseOptions(null, null), CancellationToken.None);
    }
}
```

Write the three fakes in the same folder, modelled on the fakes the existing `GarminActivityServiceTests` use.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/RouteTimer.Services.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~GarminCourseServiceTests`
Expected: FAIL — `GarminCourseService` does not exist.

- [ ] **Step 3: Add the persistence columns**

In `PredictionEntity.cs`:

```csharp
    public long? GarminCourseId { get; set; }
    public DateTimeOffset? GarminCourseUploadedAt { get; set; }
```

Map them as nullable in `OnModelCreating`, then:

```bash
dotnet ef migrations add AddPredictionGarminCourse --project src/RouteTimer.Persistence/RouteTimer.Persistence.csproj --startup-project src/RouteTimer.Api/RouteTimer.Api.csproj
dotnet ef migrations has-pending-model-changes --project src/RouteTimer.Persistence/RouteTimer.Persistence.csproj --startup-project src/RouteTimer.Api/RouteTimer.Api.csproj
```

Expected from the second command: "No changes have been made to the model since the last migration."

Add to `IPredictionRepository`:

```csharp
    Task RecordGarminCourseAsync(Guid predictionId, long courseId, DateTimeOffset uploadedAt, CancellationToken cancellationToken);
```

Implement it in `PredictionRepository` with a single tracked update, and surface `GarminCourseId` and `GarminCourseUploadedAt` on `PredictionSummary`, `PredictionDetail`, and `PredictionSummaryResponse`, updating both mapping methods in `PredictionEndpoints` and every construction site the compiler flags — including `Predictions.razor`'s optimistic `PredictionSummaryResponse` for a queued prediction, which passes `null` for both.

- [ ] **Step 4: Write the service**

`src/RouteTimer.Services/Garmin/GarminCourseService.cs`:

```csharp
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Garmin;

public sealed record GarminCourseOptions(string? Name, string? ActivityType);

public sealed record GarminCourseCreation(long CourseId, string CourseName, string CourseUrl);

public sealed class GarminCourseService(
    IGarminAdapterClient adapter,
    IGarminConnectionRepository connections,
    IPredictionRepository predictions,
    IGarminTokenProtector protector,
    GarminOperationGate gate,
    TimeProvider timeProvider)
{
    private const string DefaultActivityType = "road_biking";

    public Task<GarminCourseCreation> CreateCourseAsync(
        Guid predictionId,
        GarminCourseOptions options,
        CancellationToken cancellationToken) =>
        // The gate is why this is not a plain call: a course push must not interleave with an
        // activity import or a session validation, because all three share one Garmin session and
        // each one can rotate its tokens.
        gate.RunAsync(async token =>
        {
            var connection = await connections.GetAsync(token)
                ?? throw new GarminConnectionRequiredException();
            if (connection.State == "reconnect-required")
            {
                throw new GarminReconnectRequiredException();
            }

            var source = await predictions.GetGpxSourceAsync(predictionId, token)
                ?? throw new PredictionMissingException();

            // Always the untimed variant, whatever the rider last downloaded: a timestamped track
            // is what makes some importers treat a course as an activity.
            var gpx = PredictionGpxWriter.Write(source, timed: false);
            var fileName = PredictionGpxWriter.SuggestFileName(source.RouteName);
            var (gain, loss) = ElevationTotals(source);

            var created = await adapter.CreateCourseAsync(
                protector.Unprotect(connection.Token),
                new GarminCourseRequest(
                    fileName,
                    options.Name ?? source.RouteName,
                    options.ActivityType ?? DefaultActivityType,
                    source.Description,
                    gain,
                    loss,
                    System.Text.Encoding.UTF8.GetBytes(gpx)),
                token);

            var now = timeProvider.GetUtcNow();
            await connections.SaveAsync(
                connection with
                {
                    Token = protector.Protect(created.TokenJson),
                    LastValidatedAt = now,
                    UpdatedAt = now
                },
                token);
            await predictions.RecordGarminCourseAsync(predictionId, created.CourseId, now, token);

            return new GarminCourseCreation(
                created.CourseId,
                created.CourseName,
                $"https://connect.garmin.com/modern/course/{created.CourseId}");
        }, cancellationToken);

    private static (double Gain, double Loss) ElevationTotals(PredictionGpxSource source)
    {
        double gain = 0, loss = 0;
        var ordered = source.Segments.OrderBy(segment => segment.Sequence).ToList();
        for (var index = 1; index < ordered.Count; index++)
        {
            var delta = ordered[index].ElevationMetres - ordered[index - 1].ElevationMetres;
            if (delta > 0)
            {
                gain += delta;
            }
            else
            {
                loss -= delta;
            }
        }

        return (gain, loss);
    }
}

public sealed class PredictionMissingException() : Exception("The prediction was not found.");
```

`PredictionNotCompleteException` propagates from `PredictionGpxWriter` unchanged.

- [ ] **Step 5: Add the endpoint**

Add to `ErrorCodes.cs`:

```csharp
    public const string GarminCourseRejected = "garmin-course-rejected";
```

Add to `PredictionContracts.cs`:

```csharp
public sealed record CreateGarminCourseRequest(string? Name, string? ActivityType);

public sealed record GarminCourseResponse(long CourseId, string CourseName, string CourseUrl);
```

In `PredictionEndpoints`:

```csharp
        routes.MapPost("/api/predictions/{id:guid}/garmin-course", CreateGarminCourseAsync);
```

The handler maps `PredictionMissingException` to `404`/`prediction-not-found`, `PredictionNotCompleteException` to `409`/`prediction-not-complete`, `GarminAdapterError.CourseRejected` to `422`/`garmin-course-rejected` via `ApiProblems.Create(422, ...)`, and reuses the existing Garmin exception mapping in `GarminEndpoints` for connection, rate-limit, and availability failures — extract that mapping into a shared static helper rather than copying it.

Register `GarminCourseService` in `Program.cs` beside `GarminActivityService`.

- [ ] **Step 6: Add endpoint tests**

In `PredictionEndpointsTests.cs`, assert: `404` for an unknown prediction; `409` for a queued one; `200` with the course id and URL on success; and `422` with `garmin-course-rejected` when the fake adapter throws `CourseRejected`.

- [ ] **Step 7: Run the full suite and commit**

Run: `dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false`
Expected: PASS.

```bash
git add src tests
git commit -m "feat(api): push a prediction to Garmin Connect as a course"
```

---

## Task 20: Offer the course push in the UI

**Files:**
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`, `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`, `RouteTimerApiClient.cs`, `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`
- Test: `tests/RouteTimer.Client.Tests/PredictionDetailPageTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Offers_to_send_a_completed_prediction_to_garmin()
{
    var predictionId = Guid.NewGuid();
    api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(CompletedDetail(predictionId));
    api.OnCreateGarminCourseAsync = (_, _, _) => Task.FromResult(
        new GarminCourseResponse(4242, "Kingston to Dorking", "https://connect.garmin.com/modern/course/4242"));

    var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

    cut.WaitForAssertion(() => cut.Find("[data-testid=prediction-send-to-garmin]")).Click();

    cut.WaitForAssertion(() => Assert.Equal(
        "https://connect.garmin.com/modern/course/4242",
        cut.Find("[data-testid=prediction-garmin-course-link]").GetAttribute("href")));
}

[Fact]
public void Links_to_an_already_pushed_course_instead_of_offering_to_push_again()
{
    var predictionId = Guid.NewGuid();
    api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(
        CompletedDetail(predictionId) with
        {
            Summary = CompletedDetail(predictionId).Summary with
            {
                GarminCourseId = 4242,
                GarminCourseUploadedAt = DateTimeOffset.UnixEpoch
            }
        });

    var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

    cut.WaitForAssertion(() =>
    {
        cut.Find("[data-testid=prediction-garmin-course-link]");
        Assert.Empty(cut.FindAll("[data-testid=prediction-send-to-garmin]"));
        cut.Find("[data-testid=prediction-send-to-garmin-again]");
    });
}

[Fact]
public void Confirms_before_pushing_a_second_time()
{
    var predictionId = Guid.NewGuid();
    var pushes = 0;
    api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(
        CompletedDetail(predictionId) with
        {
            Summary = CompletedDetail(predictionId).Summary with { GarminCourseId = 4242 }
        });
    api.OnCreateGarminCourseAsync = (_, _, _) =>
    {
        pushes++;
        return Task.FromResult(new GarminCourseResponse(4243, "R", "https://connect.garmin.com/modern/course/4243"));
    };

    var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

    cut.WaitForAssertion(() => cut.Find("[data-testid=prediction-send-to-garmin-again]")).Click();
    Assert.Equal(0, pushes);

    cut.Find("[data-testid=prediction-send-to-garmin-confirm]").Click();
    cut.WaitForAssertion(() => Assert.Equal(1, pushes));
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~PredictionDetailPageTests`
Expected: FAIL — the elements and `OnCreateGarminCourseAsync` do not exist.

- [ ] **Step 3: Add the client method**

In `IRouteTimerApiClient.cs`:

```csharp
    Task<GarminCourseResponse> CreateGarminCourseAsync(Guid predictionId, CreateGarminCourseRequest request, CancellationToken ct);
```

In `RouteTimerApiClient.cs`:

```csharp
    public Task<GarminCourseResponse> CreateGarminCourseAsync(Guid predictionId, CreateGarminCourseRequest request, CancellationToken ct) =>
        SendJsonAsync<GarminCourseResponse>(HttpMethod.Post, $"/api/predictions/{predictionId}/garmin-course", request, ct);
```

Add the matching fake delegate.

- [ ] **Step 4: Add the UI**

In `PredictionDetail.razor`, beside the GPX links: an activity type `<select>` (Road cycling default, Cycling, Gravel, Mountain biking); a `prediction-send-to-garmin` button when `Summary.GarminCourseId is null`; a `prediction-garmin-course-link` anchor to `https://connect.garmin.com/modern/course/{id}` when it is set, with a `prediction-send-to-garmin-again` button whose click reveals a `prediction-send-to-garmin-confirm` button. Show failures through the existing `ProblemMessage` component, and disable the buttons while a push is in flight.

- [ ] **Step 5: Run the tests and commit**

Run: `dotnet test tests/RouteTimer.Client.Tests --no-restore -p:UseSharedCompilation=false`
Expected: PASS.

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat(client): send a prediction to Garmin Connect as a course"
```

---

## Task 21: Configuration, deployment, and documentation

**Files:**
- Modify: `run.sh`, `run.ps1`, `deploy/docker-compose.yml`, `deploy/docker-compose.local.yml`, `RUNBOOK.md`, `work-left-to-do.md`

- [ ] **Step 1: Generate the new encryption key in the run scripts**

In `run.sh`, beside the existing `Garmin__TokenEncryptionKey` generation, generate `GoogleMaps__KeyEncryptionKey` the same way, writing it to the same env file and not regenerating it when it is already present. A regenerated key would silently orphan the stored API key.

In `run.ps1`, make the equivalent change using the same cmdlets the Garmin key already uses.

- [ ] **Step 2: Pass it through Compose**

Add `GoogleMaps__KeyEncryptionKey: ${GOOGLE_MAPS_KEY_ENCRYPTION_KEY}` to the API service environment in both `deploy/docker-compose.yml` and `deploy/docker-compose.local.yml`, following the Garmin key's existing entry.

- [ ] **Step 3: Document it**

In `RUNBOOK.md`, add a subsection under the configuration section covering: what `GoogleMaps__KeyEncryptionKey` protects; that it is optional and its absence disables saving but not conversion; that losing it means the rider re-enters their Google Maps key; and that it must be included in backups alongside the Garmin key. Add a short "Predicting from a Google Maps route" section describing the rider-facing flow, the three Google products the key needs enabled (Maps JavaScript API, Directions API, Elevation API), and that HTTP referrer restrictions must allow the deployment's own origin.

Add a "Sending a prediction to Garmin" subsection noting that course creation uses undocumented Garmin endpoints that can change, and that the GPX download is the fallback.

- [ ] **Step 4: Update the outstanding-work note**

In `work-left-to-do.md`, record that the Google Maps route builder, encrypted key storage, prediction GPX export, and Garmin course push are implemented, and reference this plan and its spec.

- [ ] **Step 5: Verify a clean run**

Run: `dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false`
Run: `cd garmin-adapter && python -m pytest -q`
Expected: both PASS.

- [ ] **Step 6: Commit**

```bash
git add run.sh run.ps1 deploy RUNBOOK.md work-left-to-do.md
git commit -m "docs: document the Google Maps key, route builder, and course push"
```

---

## Self-review notes

**Spec coverage.** Every specification section maps to a task: the MapToGarmin port to Tasks 2–5; mandatory elevation to Task 5 Step 4 and Task 12 Step 3; short-link expansion to Tasks 6–7; secret protection and key persistence to Tasks 8–11; the two-mode panel, travel mode, and rider disclosure to Task 12; prediction GPX generation and both variants to Tasks 13–15; the Garmin course flow, adapter operation, orchestration, and recorded course id to Tasks 16–20; error codes distributed across Tasks 6, 10, 14, and 19; configuration and documentation to Task 21; and the feasibility gate to Task 1.

**Known cross-task couplings to watch during execution.**

- Task 19 adds two properties to `PredictionSummaryResponse`. Task 12's `SubmitBuiltRouteAsync` and the existing `SubmitAsync` both construct that record optimistically for a queued prediction, so both construction sites need the two new `null` arguments when Task 19 lands. The compiler will find them.
- `PredictionGpxSource` is defined in Task 13 and consumed by Tasks 14 and 19. `PredictionNotCompleteException` is defined once, in Task 13, and caught in Tasks 14 and 19.
- `ProtectedSecret` from Task 8 is the type stored by Task 9's `GoogleMapsCredentialRecord` and produced by Task 10's service.
- `BuiltRoute` is defined in Task 12 and used only there.
- The Garmin exception mapping helper that Task 19 Step 5 extracts is shared with `GarminEndpoints`; extract rather than duplicate.
