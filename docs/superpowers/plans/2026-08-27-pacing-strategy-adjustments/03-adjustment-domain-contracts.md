[← Back to plan overview](README.md)

# Task 3: Define adjustment domain types, contracts, validation, and canonical JSON

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/AdjustmentState.cs`
- Create: `src/RouteTimer.Domain/Adjustments/AdjustmentWarningCodes.cs`
- Create: `src/RouteTimer.Domain/Adjustments/PacingStrategyDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/PacingStrategyReport.cs`
- Create: `src/RouteTimer.Domain/Adjustments/PredictionAdjustmentAnnotation.cs`
- Create: `src/RouteTimer.Contracts/Adjustments/PacingStrategyContracts.cs`
- ~~Create: `src/RouteTimer.Contracts/Adjustments/PredictionAdjustmentContracts.cs`~~ deferred to Task 6 (see note below)
- Create: `src/RouteTimer.Services/Adjustments/PacingStrategyJson.cs`
- Create: `src/RouteTimer.Services/Adjustments/PacingStrategyValidationException.cs`
- Modify: `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`
- Test: `tests/RouteTimer.Domain.Tests/Adjustments/PacingStrategyDefinitionTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/PacingStrategyJsonTests.cs`

**Implementation note (deviation from plan):** "Add the exact strategy records approved in the
design" in Step 2 read literally would mean building all five concrete domain `Definition`/`Report`
record subtypes now — but Tasks 8, 10, 11, 12, and 13 each separately list those same domain files
as files *they* create. As actually implemented, Task 3 delivers only the abstract
`PacingStrategyDefinition`/`PacingStrategyReport` union (the enum plus the two abstract records) and
the full closed **wire-format** request union in `PacingStrategyContracts.cs` — the Contracts project
has zero project references (not even to Domain), so its five concrete request DTOs are self-contained
and fully testable without any concrete domain type existing yet. The concrete domain `Definition`
and `Report` subtypes for each strategy are built in that strategy's own delivery task (8, 10, 11, 12,
13), alongside the algorithm that actually needs them, avoiding speculative, untested domain modeling.
One consequence: `PredictionAdjustmentContracts.cs` (adjustment submission/summary/detail/segment
responses) was **not** created in this task, since its detail response needs a typed report union that
only makes sense once at least one concrete report subtype exists. It is created in
[Task 6](06-nested-apis-and-capabilities.md) instead, where the nested endpoints that actually return
it are wired up. `PacingStrategyJson`'s `Canonicalize<T>`/`Deserialize<T>` are generic over the caller's
own concrete subtype rather than the polymorphic base, so no `[JsonPolymorphic]` attributes are needed
on the domain side yet; those get added once a concrete subtype's discriminator is known (starting in
Task 8).

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
