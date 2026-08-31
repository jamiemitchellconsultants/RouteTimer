[← Plan overview](README.md)

# Additive Regression Primitives Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Train and evaluate deterministic, application-owned elastic-net and piecewise-linear GAM artifacts without an opaque model runtime.

**Architecture:** Robust median/IQR scaling feeds a weighted coordinate-descent solver. Elastic-net uses the original scaled features; GAM adds per-feature linear-hinge bases at fixed training quantiles. Five Huber IRLS rounds limit unusual rides.

**Tech Stack:** Pure C# numerical code, RouteTimer Domain/Services, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Add no ML.NET, Python, native, network, or binary model dependency.
- Training and evaluation must be deterministic for identical ordered rows.
- Store only application-owned coefficients, scaling, knots, and version strings.
- Reject non-finite input/output and mismatched feature dimensions.

### Task 5: Implement robust elastic-net and additive regressors

**Files:**

- Create: `src/RouteTimer.Domain/Ai/AiModelArtifacts.cs`
- Create: `src/RouteTimer.Services/Ai/Models/AiModelRow.cs`
- Create: `src/RouteTimer.Services/Ai/Models/RobustFeatureScaler.cs`
- Create: `src/RouteTimer.Services/Ai/Models/WeightedElasticNetSolver.cs`
- Create: `src/RouteTimer.Services/Ai/Models/AiRegressorTrainer.cs`
- Create: `src/RouteTimer.Services/Ai/Models/AiRegressorEvaluator.cs`
- Create: `src/RouteTimer.Services/Ai/Models/AiCandidateCatalog.cs`
- Create: `tests/RouteTimer.Domain.Tests/Ai/AiModelArtifactTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Models/RobustFeatureScalerTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Models/WeightedElasticNetSolverTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Models/AiRegressorTrainerTests.cs`

**Interfaces:**

```csharp
public enum AiRegressorKind { ElasticNet, GeneralizedAdditive }
public sealed record AiModelRow(Guid ActivityId, AiFeatureVector Features, double Target);
public sealed record AiCandidateDefinition(
    AiRegressorKind Kind,
    double Lambda,
    double L1Ratio,
    int StableOrder);

public sealed class AiRegressorTrainer
{
    public AiRegressorArtifact Train(
        IReadOnlyList<AiModelRow> rows,
        AiCandidateDefinition candidate);
}

public sealed class AiRegressorEvaluator
{
    public double Predict(AiRegressorArtifact artifact, AiFeatureVector features);
    public IReadOnlyList<double> ExplainByFeature(
        AiRegressorArtifact artifact, AiFeatureVector features);
}
```

The artifact shape is the stable record in `README.md`. `Kind` persists as `elastic-net` or `gam`. The fixed catalog is:

```text
ElasticNet: lambda 0.001, 0.01, 0.1 crossed with L1 ratio 0, 0.5, 1
GAM:        lambda 0.01, 0.1, 1 with L1 ratio 0
```

Stable order is exactly the order above, with lambda varying before L1 ratio for ElasticNet.

- [ ] **Step 1: Write failing artifact and scaler tests**

Assert artifacts reject unknown kind/schema, non-finite intercept/coefficient/knot, scaling count mismatch, non-increasing knots, smooth feature index outside the linear coefficient range, and non-empty smooth terms for elastic-net. Assert median and IQR on odd/even data, `scale=1` for zero IQR, no input mutation, and deterministic ordered output.

- [ ] **Step 2: Implement domain validation and robust scaling**

Use linear-interpolated percentile ranks `p * (n - 1)`, matching the repository percentile convention. Transform `(x - median) / scale`; never centre or scale the target.

- [ ] **Step 3: Write failing weighted-solver tests**

Cover exact intercept-only data, one-feature slope, soft-thresholding a coefficient to zero, L2 shrinkage, observation weights, a constant column, stable results across repeated calls, cancellation, and non-finite input rejection.

- [ ] **Step 4: Implement coordinate descent**

For each of at most 1,000 sweeps, update the intercept to the weighted mean residual, then each coefficient using partial residuals:

```text
rho = sum(weight * x_j * residual_without_j)
z   = sum(weight * x_j^2) + lambda * (1 - l1Ratio)
beta_j = softThreshold(rho, lambda * l1Ratio) / z
```

Stop when the maximum absolute parameter change is `< 1e-8`. Treat `z <= 1e-12` as coefficient zero. Throw `AiModelTrainingException("ai-regression-not-converged")` after the sweep cap.

- [ ] **Step 5: Write failing trainer/evaluator tests**

Use synthetic rows to recover a linear target, a piecewise slope change at a known quantile, and a strong outlier. Assert the robust model remains closer to the non-outlier relationship than an unweighted least-squares fixture. Assert GAM emits at most four unique knots per feature at 20/40/60/80 percentiles, elastic emits none, evaluator predictions match manual artifact calculations, per-feature contributions plus intercept sum to the prediction, and no contribution is non-finite.

- [ ] **Step 6: Implement robust training and GAM basis**

Start weights at 1. Run five IRLS rounds. After each solve, compute residual median absolute deviation, scale it by `1.4826`, set threshold `1.345 * max(scale, 1e-9)`, and set each weight to `min(1, threshold / abs(residual))` with exact-zero residual weight 1.

For GAM, expand every scaled feature into its linear value plus `max(0, x - knot)` for each unique quantile knot. Fit the expanded matrix with the same solver. Persist original-feature linear coefficients separately and group hinge coefficients into `AiSmoothTerm`; the evaluator applies captured scaling, the linear term, then hinges. `ExplainByFeature` groups each feature's linear and hinge effects. Do not extrapolate feature clipping here; support gates own that decision.

- [ ] **Step 7: Run numerical and domain tests**

```bash
dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj --no-restore --filter FullyQualifiedName~AiModelArtifactTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~RobustFeatureScalerTests|FullyQualifiedName~WeightedElasticNetSolverTests|FullyQualifiedName~AiRegressorTrainerTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 8: Commit and push**

```bash
git add src/RouteTimer.Domain/Ai src/RouteTimer.Services/Ai/Models tests/RouteTimer.Domain.Tests/Ai tests/RouteTimer.Services.Tests/Ai/Models
git commit -m "feat: add local additive AI models"
git push
git status --short
```

Expected: successful push and empty status.
