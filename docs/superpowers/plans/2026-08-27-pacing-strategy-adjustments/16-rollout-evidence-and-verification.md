[← Back to plan overview](README.md)

# Task 16: Add rollout evidence and complete system verification

**Files:**

- Create: `docs/pacing-strategies/backtesting.md`
- Modify: `docs/pacing-strategies/06-cross-cutting-rollout.md`
- Modify: `README.md` only if it currently documents feature flags or operator configuration.
- Modify: deployment configuration examples that already carry application feature flags; do not invent a second configuration system.

**Step 1: Add a deterministic back-testing harness or fixture set**

Use representative retained route/model fixtures for flat, rolling, mountainous, short, and long routes. Record baseline invariance and, for each enabled strategy, finite output, sequence parity, deterministic reruns, evaluation count, and expected direction of time/power changes. Keep physiological interpretation out of pass/fail criteria.

**Step 2: Document staged enablement**

Document this order and rollback:

1. deploy schema and predictor refactor with all flags off;
2. enable parent plus segment gains for internal users;
3. enable time target and NP/IF after search telemetry is acceptable;
4. enable zones after provenance/report review;
5. enable match-burning last;
6. disable an individual strategy to stop new submissions while retaining access to historical children; and
7. disable the parent to hide creation while baseline prediction remains unaffected.

Include operational signals: adjustment queue age, runtime, evaluations per job, cancellation/failure counts by stable diagnostic code, and publication conflicts. Do not log full strategy JSON.

**Step 3: Run narrative verification**

Use the repository-configured Narrative compiler to check the correction fragment and generated `Narrative.md`:

```bash
node /private/tmp/RouteTimer-Narrative-tool/bin/narrative.mjs check --config .project-narrative.json
```

Expected: generated narrative is current and the correction cites `docs-add-pacing-strategy-implementation-plans`.

**Step 4: Run the complete solution**

```bash
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: every Domain, Services, Persistence, API, Client, and EndToEnd test passes. Record project-by-project counts in the PR.

**Step 5: Inspect the final diff**

```bash
git status --short
git diff --check
git diff --stat main...HEAD
```

Confirm:

- no baseline submission or response compatibility break;
- no adjustment export action;
- flags default off;
- generated migration and model snapshot agree;
- `Narrative.md` was generated, not hand-edited;
- no secrets or route/model payloads appear in logs; and
- all five strategies have service, API, persistence, and client coverage.

**Step 6: Commit**

```bash
git add docs README.md
git commit -m "docs: add pacing adjustment rollout evidence"
```

**Step 7: Push and summarize**

```bash
git push
```

Summarize the change for this task: the back-testing harness and its pass/fail criteria, the staged enablement/rollback plan, and the full-solution verification results (project-by-project test counts, final diff checks). This is the final review checkpoint (production readiness) — summarize the whole plan's outcome and flag any open risk before merge.
