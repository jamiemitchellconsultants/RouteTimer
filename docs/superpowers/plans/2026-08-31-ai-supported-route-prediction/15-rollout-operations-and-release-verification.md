[← Plan overview](README.md)

# Rollout, Operations, and Release Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete shadow/comparison/automatic serving controls, privacy-safe telemetry and runbook guidance, then prove every fallback, compatibility, and acceptance criterion with fresh full-suite evidence.

**Architecture:** Typed `Meter` instruments observe aggregate model/build/serving outcomes without route or feature values. Configuration keeps build and serving independently reversible; adversarial fixtures exercise corrupt artifacts, concurrency, cancellation, and legacy rows before rollout.

**Tech Stack:** .NET `System.Diagnostics.Metrics`, ASP.NET options/configuration, Docker deployment docs, all test projects and npm tests.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Defaults remain `BuildEnabled=false`, `ServingStage=Disabled`.
- Shadow performs training only; Comparison evaluates but persists/returns deterministic; Automatic may serve only Published supported models.
- Metrics/logs contain no coordinates, activity names, feature vectors, coefficients, route summaries, exact multipliers, or model JSON.
- Rollback changes configuration only and deletes no models/examples/provenance.
- Request final code review and use verification-before-completion before claiming the series complete.

### Task 15: Harden, document and verify release behaviour

**Files:**

- Create: `src/RouteTimer.Services/Ai/AiTelemetry.cs`
- Modify: `src/RouteTimer.Services/Ai/Training/BuildAiModelJobHandler.cs`
- Modify: `src/RouteTimer.Services/Ai/Prediction/AiPredictionRunner.cs`
- Modify: `src/RouteTimer.Api/Ai/AiOptions.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `src/RouteTimer.Api/appsettings.json`
- Modify: `deploy/docker-compose.yml`
- Modify: `deploy/docker-compose.local.yml`
- Modify: `RUNBOOK.md`
- Modify: `deploy/README.md`
- Create: `tests/RouteTimer.Services.Tests/Ai/AiTelemetryTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/AiAdversarialWorkflowTests.cs`
- Modify: configuration, logging/privacy, prediction compatibility, deletion, job queue and migration tests across existing projects

**Interfaces:**

Use one meter `RouteTimer.Ai` with bounded tags only:

```text
routetimer.ai.build.duration                 histogram seconds; tags outcome, publication
routetimer.ai.examples                       counter; tags outcome=derived|reused|excluded
routetimer.ai.validation.folds               histogram; tags mode=typical|today
routetimer.ai.prediction                     counter; tags requested, effective, fallback
routetimer.ai.route_gate                     counter; tags outcome=supported|rejected, reason
routetimer.ai.runtime_fallback               counter; tag reason
```

Never tag activity ID, prediction ID, model ID, user ID, coordinates, counts unique to one ride, or exception message.

- [ ] **Step 1: Write failing serving-stage matrix tests**

Assert all combinations of BuildEnabled and stage:

- Disabled + false: no AI enqueue/evaluation;
- Disabled + true: builds may run, serving deterministic;
- Shadow + true: builds run, prediction does not evaluate artifact;
- Comparison + true: artifact evaluates and telemetry compares, stored/final result remains deterministic;
- Automatic + true: supported Published artifact may serve;
- any stage + false: existing artifacts may serve only if stage is Automatic/Comparison as configured, while no new build queues.

Choose and document the last rule exactly: `BuildEnabled` controls training only; `ServingStage` controls existing artifact use. Do not couple them implicitly.

- [ ] **Step 2: Implement aggregate telemetry and stage completion**

Inject a singleton `AiTelemetry` wrapper around `Meter`. Time builds with `Stopwatch.GetElapsedTime`. Emit only after operations settle. Comparison records aggregate absolute time error only when the real outcome later becomes known during historical evaluation; production new-route requests have no actual time, so record AI-versus-baseline delta in an untagged histogram rather than claiming accuracy. Do not persist comparison segments.

- [ ] **Step 3: Write adversarial workflow tests**

Cover corrupt/oversized artifact JSON, unknown versions/enums/reasons, non-finite evaluator output, multiplier exponent overflow, empty bound intersection, route exactly on/off each critical range, four/five neighbours, stale/future confirmation, profile change, deterministic algorithm change, activity insert/delete/re-enrichment changing prefix digest, cancellation during replay/inner training/outer scoring/publication/prediction rerun, AI build successor coalescing, rejected build with old current, prediction deletion with AI FK, and legacy prediction/model rows.

Assert every AI runtime case returns the exact valid deterministic result and a safe closed fallback, while cancellation and deterministic failure still propagate normally.

- [ ] **Step 4: Add privacy/logging tests**

Capture logs and metric tags for failed build/evaluation. Seed distinctive coordinates, filenames, activity IDs, feature values, coefficients, multiplier, and model JSON, then assert none appears. Safe diagnostics may include fixed reason code, aggregate qualifying count, fold count, duration, and publication state.

- [ ] **Step 5: Document configuration, rollout and rollback**

Add:

```text
Ai__BuildEnabled=false
Ai__ServingStage=Disabled|Shadow|Comparison|Automatic
```

to compose environments with safe defaults. RUNBOOK sequence is: finish weather backfill; enable build + Shadow; inspect readiness/build/validation; move to Comparison; inspect fallback/stage metrics; move to Automatic; rollback by setting ServingStage Disabled; optionally stop builds separately. State that all ML stays local and that the weather prerequisite retains its own Open-Meteo disclosure.

- [ ] **Step 6: Audit every acceptance criterion**

Create a checklist in the commit message preparation notes mapping all 13 spec acceptance criteria to exact automated tests. Add a missing test before proceeding; do not mark a criterion satisfied by prose or manual reasoning alone, except operator rollout transitions which require configuration/DI tests plus documented manual inspection.

- [ ] **Step 7: Run format/static checks and every automated suite fresh**

```bash
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
cd src/RouteTimer.Client && npm test
cd ../..
git diff --check
git status --short
```

Expected: every command exits 0; only Task 15 files are modified before commit. If `--no-restore` fails because packages are new/missing, run `dotnet restore RouteTimer.slnx` once, then rerun the exact test command.

- [ ] **Step 8: Perform deterministic manual fixture inspection**

Using test/fake data only, inspect one each of: collecting readiness, baseline-still-best, AI Typical success, AI Today success, stale Today to Typical, unsupported route to deterministic, and legacy prediction. Confirm copy, final/baseline values, and no route-match confidence wording. Do not upload private ride data or call live Garmin/Open-Meteo for this check.

- [ ] **Step 9: Commit, push, and request final review**

```bash
git add src/RouteTimer.Services/Ai src/RouteTimer.Api/Ai src/RouteTimer.Api/Program.cs src/RouteTimer.Api/appsettings.json deploy RUNBOOK.md tests
git commit -m "feat: complete AI prediction rollout controls"
git push
git status --short
```

Expected: successful push and empty status. Request review for Tasks 12-15, then use `superpowers:verification-before-completion` with fresh full-suite output before reporting completion. The implementation PR must satisfy the narrative label/body contract before merge.
