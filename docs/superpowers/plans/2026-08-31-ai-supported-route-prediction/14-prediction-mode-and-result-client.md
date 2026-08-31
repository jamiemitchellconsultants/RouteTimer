[← Plan overview](README.md)

# Prediction Mode and Result Client Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the rider request Typical or Today and clearly explain AI-supported comparisons, route gating, Today fallback, or deterministic fallback on completed predictions.

**Architecture:** The prediction form sends one explicit mode field. A focused result component renders captured provenance; it never recomputes or infers AI state from current model status.

**Tech Stack:** Blazor WebAssembly, multipart API client, bUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Typical is selected by default.
- There is no “Use AI” checkbox; server gates activate automatically.
- Completed prediction display uses captured fields, not current readiness/model.
- Label multiplier as learned effort adjustment, never time correction or physiological truth.
- Route match is similarity, not confidence.

### Task 14: Add mode selection and AI result explanation

**Files:**

- Create: `src/RouteTimer.Client/Components/Ai/PredictionAiSummary.razor`
- Create: `src/RouteTimer.Client/Components/Ai/PredictionAiSummary.razor.css`
- Modify: `src/RouteTimer.Client/Pages/Predictions.razor`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- Modify: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/Formatting/RouteTimerFormat.cs`
- Modify: `src/RouteTimer.Client/Formatting/RouteTimerText.cs`
- Create: `tests/RouteTimer.Client.Tests/Components/PredictionAiSummaryTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionsPageTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionDetailPageTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/Api/RouteTimerApiClientTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`

**Interfaces:**

Change the client submission call to accept an explicit mode:

```csharp
Task<QueuedPredictionResponse> SubmitPredictionAsync(
    ClientFileUpload upload,
    string mode,
    CancellationToken ct);
```

`mode` is exactly lower-case `typical` or `today`; client code never sends display text.

- [ ] **Step 1: Write failing mode-form and multipart tests**

Assert Typical initially selected, Today copy explains current-history dependency, toggling sends exactly one `mode=today` multipart field for GPX and route-builder submissions, retry preserves selected mode, new page instance returns to Typical, and mode controls have accessible labels/fieldset legend.

- [ ] **Step 2: Implement explicit mode submission**

Use a two-option radio group or existing segmented-control semantics. Thread selected mode through every prediction creation path into the API client. Keep file/route-builder validation and job polling unchanged.

- [ ] **Step 3: Write failing result-component tests**

Cover:

- AiTypical: final time, baseline time, signed effort adjustment, route match, supporting rides, comparable median-P90 range, model version;
- AiToday: same plus Today label;
- Today requested/AiTypical effective: stale/unavailable/state fallback text then AI Typical comparison;
- Deterministic route rejection: specific longer/steeper/curvature/insufficient-neighbour explanation;
- Deterministic unavailable/disabled/runtime fallback: neutral safe copy;
- legacy null metadata: component absent and existing result unchanged;
- contribution copy such as “Long duration reduced expected effort”, ordered as persisted, with unknown contribution codes omitted;
- no `confidence` word next to route match; and
- negative adjustment formatted with a true minus sign and positive with `+`.

- [ ] **Step 4: Implement result component and copy mapping**

Use captured final `movingSeconds` and `deterministicBaselineSeconds`. `aiEffortAdjustmentPercent` is already `(multiplier - 1) * 100`; do not derive it from times. Map known fixed-schema contribution codes and the two validated directions to short rider-facing sentences; omit unknown codes rather than exposing them. Format comparable values as percentages and text “Comparable validation error: X–Y”. Unknown fallback uses “AI support was not used for this prediction.”

- [ ] **Step 5: Integrate completed detail only**

Render after the main prediction summary and before charts. Do not change chart segments, GPX, Garmin, pacing-adjustment comparison, or current model-status sections. Existing historical predictions remain visually identical.

- [ ] **Step 6: Run client/API-client regressions**

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionAiSummaryTests|FullyQualifiedName~PredictionsPageTests|FullyQualifiedName~PredictionDetailPageTests|FullyQualifiedName~RouteTimerApiClientTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 7: Commit and push**

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat: present AI-supported predictions"
git push
git status --short
```

Expected: successful push and empty status.
