[← Plan overview](README.md)

# Training Readiness Client Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show restrained AI evidence progress, model evaluation state, and Today freshness on Training with compact status on Home and Predictions.

**Architecture:** A reusable component renders the nested status contract. Training enables the manual-current action and refreshes status after success; shared surfaces render a compact read-only variant.

**Tech Stack:** Blazor WebAssembly, existing API client, CSS, bUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Text only: no mockups, diagrams, badges, points, streaks, confetti, animation, or leaderboards.
- Do not call readiness a probability, accuracy, confidence, or guarantee.
- Show deterministic model readiness separately and preserve existing status content.
- Map only known server codes; unknown codes use neutral fallback copy and never display raw code as coaching advice.

### Task 13: Add AI readiness and freshness UI

**Files:**

- Create: `src/RouteTimer.Client/Components/Ai/AiReadiness.razor`
- Create: `src/RouteTimer.Client/Components/Ai/AiReadiness.razor.css`
- Modify: `src/RouteTimer.Client/Components/ModelStatus.razor`
- Modify: `src/RouteTimer.Client/Pages/Training.razor`
- Modify: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/Formatting/RouteTimerText.cs`
- Create: `tests/RouteTimer.Client.Tests/Components/AiReadinessTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/TrainingPageTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/Components/SharedStatusComponentTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/Api/RouteTimerApiClientTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`

**Interfaces:**

```csharp
Task<TrainingHistoryConfirmationResponse> ConfirmTrainingHistoryCurrentAsync(CancellationToken ct);

// Component parameters
[Parameter, EditorRequired] public AiModelStatusResponse Status { get; set; }
[Parameter] public bool Compact { get; set; }
[Parameter] public EventCallback ConfirmHistoryCurrent { get; set; }
[Parameter] public bool ConfirmingHistory { get; set; }
```

- [ ] **Step 1: Write failing component rendering tests**

Cover 0/68/100 percentages; exact labels `Ride count`, `Duration variety`, `Terrain variety`; progress `aria-valuemin/max/now`; strongest and next code mappings; collecting/evaluating/re-evaluating/AI-supported/baseline-best copy; Today current/stale; compact omitting action/details; unknown code neutral fallback; and absence when `ModelStatusResponse.Ai` is null.

- [ ] **Step 2: Implement component and restrained styling**

Use native `<progress max="100">` or an accessible `role=progressbar`, plain text, existing colour variables, and no animation. Display percentage rounded to nearest whole number only in text; keep contributor current/target exact. Suggested copy is “Best next addition: …”, not “You should train …”.

- [ ] **Step 3: Write failing API/manual-action tests**

Assert API client POSTs an empty request to the exact path, deserializes response, and cancellation propagates. On Training, click disables the action, success reloads model status without reloading activities, API problem renders inline, double click makes one request, and disposal cancels an in-flight request.

- [ ] **Step 4: Implement API operation and Training integration**

Show the manual action only when full status is rendered and history is not current. Copy: “My uploaded history is current” with explanatory text that Today assumes all recent rides are uploaded. Reuse `ProblemMessage`; do not toast a success that can become stale independently.

- [ ] **Step 5: Integrate compact shared status**

Render compact AI readiness under existing deterministic `ModelStatus` on Home/Predictions. Do not duplicate fetches; both already receive `ModelStatusResponse`. Update all fixture constructors with an explicit nullable AI value.

- [ ] **Step 6: Run client and API-client tests**

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~AiReadinessTests|FullyQualifiedName~TrainingPageTests|FullyQualifiedName~SharedStatusComponentTests|FullyQualifiedName~RouteTimerApiClientTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 7: Commit and push**

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat: show AI training readiness"
git push
git status --short
```

Expected: successful push and empty status.
