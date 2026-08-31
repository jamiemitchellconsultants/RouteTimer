[← Plan overview](README.md)

# Operations, Telemetry, and Release Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish privacy/operations documentation and instrumentation, then verify the complete feature and rollout path.

**Architecture:** Low-cardinality `System.Diagnostics.Metrics` instruments provider calls, enrichment state, and adjusted downloads without coordinates. README/runbook/deployment docs replace the old no-third-party-weather assumption and give exact backfill, self-hosting, rollback, and troubleshooting procedures.

**Tech Stack:** .NET `Meter`, xUnit `MeterListener`, Markdown operational docs, full solution test suite.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Metrics and logs contain no coordinates, route-linked timestamps, request URLs, API keys, filenames, or raw weather payloads.
- Keep metric dimensions bounded to operation/outcome/provider-version categories.
- Correct README/RUNBOOK claims that training routes never leave the machine.
- Do not edit generated `Narrative.md`.

### Task 12: Add telemetry/docs and perform release verification

**Files:**

- Create: `src/RouteTimer.Services/Weather/WeatherTelemetry.cs`
- Modify: `src/RouteTimer.Api/Weather/OpenMeteoWeatherProvider.cs`
- Modify: `src/RouteTimer.Services/Weather/HistoricalWeatherEnrichmentService.cs`
- Modify: `src/RouteTimer.Services/Predictions/WeatherAdjustedGpxService.cs`
- Create: `tests/RouteTimer.Services.Tests/Weather/WeatherTelemetryTests.cs`
- Modify: `README.md`
- Modify: `RUNBOOK.md`
- Modify: `deploy/README.md`
- Modify: `deploy/docker-compose.yml`
- Modify: `deploy/docker-compose.local.yml`
- Modify tests only to repair regressions caused by the complete feature; do not weaken assertions.

**Interfaces:**

```csharp
public static class WeatherTelemetry
{
    public const string MeterName = "RouteTimer.Weather";
    public static readonly Meter Meter;
    public static readonly Counter<long> ProviderRequests;
    public static readonly Histogram<double> ProviderDurationMilliseconds;
    public static readonly Counter<long> EnrichmentOutcomes;
    public static readonly Counter<long> AdjustedDownloadOutcomes;
    public static readonly Histogram<double> AdjustedDownloadDurationMilliseconds;
    public static readonly Histogram<double> AdjustedDownloadWetSegmentRatio;
}
```

Allowed tags: `operation=archive|forecast`, `outcome=success|retryable-failure|permanent-failure|cancelled`, and bounded `provider_version`. Never use IDs as tags.

- [ ] **Step 1: Write failing telemetry tests**

Use `MeterListener` to assert exactly one request count and duration per provider call, one enrichment outcome per terminal service outcome, and one adjusted-download outcome/duration/wet ratio. Assert tag keys are exactly from the allowed set and values contain no test coordinate, activity ID, prediction ID, filename, or timestamp.

- [ ] **Step 2: Implement and instrument telemetry**

Use `Stopwatch.GetTimestamp/GetElapsedTime` in `try/catch/finally`. Count cancellation distinctly and rethrow it. Record wet ratio as `wet segments / total segments` only on success. If forecast expansion performs a second provider call, record two provider metrics but one whole-download metric.

- [ ] **Step 3: Update README privacy and workflow**

State precisely: original FIT/GPX and full samples remain local, while representative route coordinates/times go to the configured Open-Meteo endpoint for enrichment/download. Document archive vs forecast use, weather-aware profile semantics, ordinary calm/dry predictions, and opt-in ephemeral download.

- [ ] **Step 4: Update RUNBOOK and deploy configuration**

Document every Weather key/default; public-service privacy and self-hosted URLs; startup backfill states and counts; provider/network failures and restart retry; legacy prediction guidance; disabling calls without deleting observations/models; observation-table backup; rollout; and rollback.

Expose environment-variable overrides in both compose files for archive/forecast URLs, model names, optional API key, and thresholds. Do not place a real key in the repository.

- [ ] **Step 5: Run repository safety scans**

```bash
rg -n "api[_-]?key=|latitude=.*longitude=|archive-api\.open-meteo.*[0-9]+\.[0-9]+" src tests deploy README.md RUNBOOK.md
rg -n "never leaves your machine|nobody else's server ever sees" README.md RUNBOOK.md deploy/README.md
```

Expected: first scan has no secret or real route; second has no stale absolute privacy claim. Inspect every match.

- [ ] **Step 6: Run every focused weather test**

```bash
dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj --no-restore --filter FullyQualifiedName~Weather -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~Weather|FullyQualifiedName~PhysicsCalibrator|FullyQualifiedName~DescentLimit|FullyQualifiedName~ModelValidator|FullyQualifiedName~BuildModel|FullyQualifiedName~RoutePredictor|FullyQualifiedName~PredictionGpx" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~Weather|FullyQualifiedName~PostgresMigration" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~Weather|FullyQualifiedName~TrainingEndpoint|FullyQualifiedName~ModelEndpoint|FullyQualifiedName~PredictionEndpoint" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~Training|FullyQualifiedName~ModelStatus|FullyQualifiedName~PredictionDetail|FullyQualifiedName~RouteTimerApiClient" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
npm test --prefix src/RouteTimer.Client
```

Expected: all pass, no zero-test filters, no live network.

- [ ] **Step 7: Run complete build and test verification**

```bash
dotnet build RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test RouteTimer.slnx --no-build -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
git status --short
```

Expected: build has zero warnings/errors, every test project passes, diff check is clean, and status lists only Task 12 changes before commit.

- [ ] **Step 8: Inspect acceptance criteria against evidence**

Read all 14 acceptance criteria in the spec. For each, identify at least one passing test or explicit documentation/config diff. If a criterion lacks evidence, add a specifically named failing test in the owning existing test file, implement the missing behavior, and rerun Step 6 plus Step 7.

- [ ] **Step 9: Commit and push**

```bash
git add src tests README.md RUNBOOK.md deploy
git commit -m "docs: operationalize weather-aware rider modelling"
git push
git status --short
git log -12 --oneline
```

Expected: successful push, empty status, and twelve distinct task commits ending with this one.

- [ ] **Step 10: Prepare the decision-bearing pull request**

Use this body and apply `narrative-required` before merge:

```markdown
## Narrative Context

Training speed was interpreted as if every ride occurred in calm, dry reference conditions, so wind, air density, and rain could bias the learned physical model and its validation.

## Narrative Decision

Persist Open-Meteo archive observations beside immutable training evidence, build a dry calm-reference rider model with weather-aware calibration/validation, and offer forecast adjustment only as an ephemeral timed-GPX download.

## Narrative Consequences

Representative route coordinates and times are disclosed to the configured weather provider; ordinary predictions remain immutable calm/dry baselines; legacy predictions must be recreated before forecast-adjusted export.
```

Do not merge unless CI passes and the label plus all three headings are present.
