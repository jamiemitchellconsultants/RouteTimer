[← Back to plan overview](README.md)

# Task 3: Define adjustment domain types, contracts, validation, and canonical JSON

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/AdjustmentState.cs`
- Create: `src/RouteTimer.Domain/Adjustments/AdjustmentWarningCodes.cs`
- Create: `src/RouteTimer.Domain/Adjustments/PacingStrategyDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/PacingStrategyReport.cs`
- Create: `src/RouteTimer.Domain/Adjustments/PredictionAdjustmentAnnotation.cs`
- Create: `src/RouteTimer.Contracts/Adjustments/PacingStrategyContracts.cs`
- Create: `src/RouteTimer.Contracts/Adjustments/PredictionAdjustmentContracts.cs`
- Create: `src/RouteTimer.Services/Adjustments/PacingStrategyJson.cs`
- Create: `src/RouteTimer.Services/Adjustments/PacingStrategyValidationException.cs`
- Modify: `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`
- Test: `tests/RouteTimer.Domain.Tests/Adjustments/PacingStrategyDefinitionTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/PacingStrategyJsonTests.cs`

**Step 1: Write failing closed-union tests**

Test all five stable discriminators, unknown-discriminator rejection, subtype/discriminator mismatch, duplicate rule IDs, non-finite values, reversed ranges, list limits, and the 64 KiB serialized limit. Also assert `AdjustmentWarningCodes.IsKnown` rejects baseline and arbitrary warning strings.

**Step 2: Add the domain union**

Use these stable values:

```csharp
public enum PacingStrategyType
{
    SegmentSpecificGains,
    NpIfTarget,
    TimeTarget,
    RpeZoneShift,
    VariableMatchBurning
}

public abstract record PacingStrategyDefinition(PacingStrategyType Type);
public abstract record PacingStrategyReport(PacingStrategyType Type);
```

Add the exact strategy records approved in the design. Keep `Definition` and `Report` immutable. Store per-segment optional values in `PredictionAdjustmentAnnotation(int? ZoneNumber, string? StrategyPhase, double? WPrimeBalanceJoules)`.

**Step 3: Add polymorphic HTTP contracts**

Annotate only the contract request and response roots with `JsonPolymorphic` and one `JsonDerivedType` per stable discriminator. The API mapper must exhaustively translate each contract subtype to a domain subtype. Do not add a strategy property to baseline submission contracts.

**Step 4: Canonicalize in services**

Configure a dedicated `JsonSerializerOptions` with deterministic camel-case property names, explicit enum strings, no indentation, and rejection of named floating-point literals. Round-trip through the expected domain subtype before persisting. Validate UTF-8 byte count after canonicalization.

**Step 5: Add public error codes**

Add stable codes for adjustment not found, baseline not ready, strategy disabled, invalid strategy, capacity inputs required, and target infeasible. Map detailed field errors later at the API boundary; never persist arbitrary validation messages as warning codes.

**Step 6: Run domain and service tests, then commit**

```bash
dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~Adjustments -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src/RouteTimer.Domain/Adjustments src/RouteTimer.Contracts/Adjustments src/RouteTimer.Services/Adjustments src/RouteTimer.Contracts/Errors tests/RouteTimer.Domain.Tests/Adjustments tests/RouteTimer.Services.Tests/Adjustments
git commit -m "feat: define pacing adjustment contracts"
```

**Step 7: Push and summarize**

```bash
git push
```

Summarize the change for this task: the closed strategy union, the polymorphic contracts, the canonical JSON rules, and the new error codes. Note any validation edge case a reviewer should re-check (list limits, byte-size limit, unknown-discriminator handling).
