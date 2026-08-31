[← Plan overview](README.md)

# AI Build Job Orchestration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Derive/cache chronological examples and evaluate a coalesced AI challenger after each successful weather-aware deterministic model build.

**Architecture:** A source service hashes each evidence prefix, replays every target, and records success/exclusion rows. A separate durable `BuildAiModel` job calculates readiness, evaluates challengers, and atomically saves the result; deterministic publication remains independent.

**Tech Stack:** Existing PostgreSQL job queue/worker, weather evidence repositories, Tasks 01-08, xUnit/API DI tests.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- `BuildModel` succeeds and remains current even if AI enqueue/build/evaluation later fails.
- Only one queued and one running AI build for `ModelSubject.Id` may exist under existing queue indexes.
- New evidence during a running build coalesces into one queued successor.
- Cancellation before `SaveEvaluationAsync` leaves current AI unchanged.
- Build is disabled by default until rollout Task 15 enables an operator-selected stage.

### Task 9: Add derived-example source and durable AI build

**Files:**

- Create: `src/RouteTimer.Api/Ai/AiOptions.cs`
- Modify: `src/RouteTimer.Domain/Jobs/AnalysisJob.cs`
- Modify: `src/RouteTimer.Services/Jobs/JobProgressReporter.cs`
- Create: `src/RouteTimer.Services/Ai/Training/AiHistoricalExampleSource.cs`
- Create: `src/RouteTimer.Services/Ai/Training/AiEvidencePrefixHasher.cs`
- Create: `src/RouteTimer.Services/Ai/Training/BuildAiModelJobHandler.cs`
- Modify: `src/RouteTimer.Services/Models/BuildModelJobHandler.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `src/RouteTimer.Api/appsettings.json`
- Create: `tests/RouteTimer.Services.Tests/Ai/Training/AiEvidencePrefixHasherTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Training/AiHistoricalExampleSourceTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Training/BuildAiModelJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/BuildModelJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Api.Tests/ConfigurationValidationTests.cs`

**Interfaces:**

```csharp
public enum AiServingStage { Disabled, Shadow, Comparison, Automatic }
public sealed record AiOptions(bool BuildEnabled, AiServingStage ServingStage)
{
    public static AiOptions Bind(IConfiguration configuration);
}

public sealed class AiEvidencePrefixHasher
{
    public byte[] Hash(
        RiderProfile profile,
        string deterministicAlgorithmVersion,
        IReadOnlyList<WeatherActivityEvidence> prefix);
}

public sealed class AiHistoricalExampleSource
{
    public Task<IReadOnlyList<AiLabeledReplay>> BuildAsync(
        RiderProfile profile,
        IReadOnlyList<WeatherActivityEvidence> chronologicalEvidence,
        CancellationToken cancellationToken);
}
```

Add `BuildAiModel` to `JobType`. Add progress stages `loading-ai-evidence`, `deriving-ai-examples`, `training-ai-typical`, `validating-ai-typical`, `training-ai-today`, `validating-ai-today`, `calibrating-ai-support`, and `saving-ai-evaluation`.

- [ ] **Step 1: Write failing prefix-hash tests**

Assert SHA-256 determinism and change on profile weights, deterministic algorithm, activity ID/order/time, cleaned sample values/count, weather provider version, or weather observation values. Assert activity name/source filename does not affect the hash. Hash canonical binary primitives with explicit little-endian encoding; do not hash JSON or culture-formatted text.

- [ ] **Step 2: Implement prefix hashing**

Write finite numeric bit patterns using `BitConverter.DoubleToInt64Bits`, UTC ticks, GUID bytes, enum integral values, and UTF-8 algorithm/provider versions into `IncrementalHash`. Include each prefix activity's cleaned physical fields and weather observations so reparse/re-enrichment invalidates all later cache keys.

- [ ] **Step 3: Write failing example-source tests**

Assert stable chronological order, no example for the first ride, prefix excludes target, cache hit uses stored features/label but still returns a fresh replay context for whole-time scoring, cache miss invokes replay then solver and upserts success, replay/solver failure upserts an exclusion, excluded cache hit skips another solve, later target receives a different prefix digest, cancellation is never cached, and fewer than 29 successes remains available for a rejected evaluation rather than throwing.

- [ ] **Step 4: Implement example source**

For each target after the first, compute prefix digest and lookup. Always call `HistoricalBaselineReplayer` to construct the ephemeral scoring context. On a matching successful cache row, combine cached vector/state/label with the fresh context after verifying baseline/actual seconds within one millisecond; otherwise recompute and replace. Do not return excluded rows to evaluator.

- [ ] **Step 5: Write failing build-job and chain tests**

Cover disabled build (no enqueue), successful deterministic save followed by one coalesced AI job, no enqueue below readiness gate, AI job missing profile/model/evidence stable failures, readiness recalculation, source progress, Published and Rejected saves, old current retention on reject/failure/cancellation, latest deterministic model ID/version/profile passed to save, and concurrent successor coalescing.

- [ ] **Step 6: Implement options, job and build chain**

Bind `Ai:BuildEnabled` default false and `Ai:ServingStage` default `Disabled`; reject unknown stage at startup. In `BuildModelJobHandler`, enqueue only after deterministic save commits and only when build enabled plus readiness `CanEvaluate`. In `BuildAiModelJobHandler`, load latest deterministic snapshot, same profile, weather-ready eligible evidence, readiness, examples, evaluator result, and save request. A rejected evaluation is a succeeded job, not worker failure.

- [ ] **Step 7: Register dependencies and verify handler discovery**

Register options singleton, AI repositories, replay/model/support/training services with appropriate singleton/scoped lifetimes, and `BuildAiModelJobHandler` as `IJobHandler`. Extend any handler-uniqueness/DI tests so every enum job type has exactly one handler.

- [ ] **Step 8: Run focused workflow and configuration tests**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~AiEvidencePrefixHasherTests|FullyQualifiedName~AiHistoricalExampleSourceTests|FullyQualifiedName~BuildAiModelJobHandlerTests|FullyQualifiedName~BuildModelJobHandlerTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter FullyQualifiedName~ConfigurationValidationTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 9: Commit and push**

```bash
git add src/RouteTimer.Api/Ai src/RouteTimer.Api/Program.cs src/RouteTimer.Api/appsettings.json src/RouteTimer.Domain/Jobs src/RouteTimer.Services/Ai/Training src/RouteTimer.Services/Jobs src/RouteTimer.Services/Models tests
git commit -m "feat: build AI challengers after rider models"
git push
git status --short
```

Expected: successful push and empty status.
