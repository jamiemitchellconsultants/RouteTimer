# Garmin activity import smoke test

This is an opt-in manual release check against a real personal Garmin Connect account. Never run it in CI or an automated test suite. Run it after first deployment and after changing the pinned Garmin dependency or adapter contract.

## Secret-safety rules

- Use the deployed HTTPS RouteTimer site. Enter the Garmin email, password, and MFA code only in the RouteTimer connection form; never place them in commands, fixtures, notes, URLs, or chat.
- Do not enable request-body or verbose HTTP logging. Do not copy Garmin tokens, cookies, token bundles, the token-encryption key, credentials, or MFA codes into test evidence.
- Before sharing any screenshot, browser export, console output, or log bundle, review it manually for credentials, MFA codes, cookies, authorization headers, Garmin tokens, token JSON, challenge IDs, the encryption key, and personal account data. Redact or discard unsafe material.

## Preconditions

1. Deploy RouteTimer and the private adapter by following [`deploy/README.md`](../deploy/README.md).
2. Use a rider account with access to at least one recent road-cycling or gravel-cycling activity. To exercise pagination, the Garmin account needs more than 50 activities.
3. Confirm the Garmin adapter has no published host port and that only RouteTimer is exposed through Caddy.
4. Record only pass/fail outcomes and safe activity metadata needed for the release check.

## Login and MFA

1. Sign in to RouteTimer as the rider and open Training.
2. In the Garmin section, enter the Garmin email and password and select **Connect**.
3. If Garmin requests MFA, enter the current MFA code. Confirm an invalid or expired challenge returns safely to a recoverable login state, then complete login with a valid code.
4. Confirm the connected state shows only safe identity metadata and never displays the password, MFA code, cookies, or token data.

## Pagination, filtering, import, and model flow

1. Confirm the first activity page is newest first and contains no more than 50 entries.
2. Confirm every visible entry is road cycling or gravel cycling. In particular, verify known indoor, e-bike, mountain-bike, running, swimming, and other activities are absent.
3. If **Load more** is available, use it and confirm the next page appends older activities without duplicating the first page. For an account with more than 50 activities, continue for at least one additional page.
4. Select one importable road or gravel activity and import it. Confirm the per-activity result reports that its FIT download was accepted (or reports an existing idempotent import if it was used previously).
5. Follow the parse-job status to completion. Confirm the imported activity appears in retained training evidence, the model rebuild completes, and projected-speed output uses the current rebuilt model. Record any invalid-FIT, download, parse, or model failure exactly as shown without copying sensitive diagnostics.
6. Try importing the same activity again and confirm it is shown as already imported rather than creating duplicate evidence or another parse job.

## Saved-token reuse, disconnect, and reconnect

1. Restart both application services with `docker compose restart routetimer routetimer-garmin-adapter` without disconnecting Garmin.
2. Reload Training and confirm RouteTimer reuses the saved encrypted token: the account remains connected and activities load without asking for the Garmin password or MFA again. A legitimate Garmin reauthentication requirement is a smoke-test failure to investigate, not a reason to record credentials.
3. Disconnect Garmin and confirm the UI returns to the connection form while the imported training activity, completed model, and model history remain available.
4. Reconnect with the Garmin login flow, completing MFA if requested. Confirm activity listing works and the previously imported activity is still marked as imported.
5. Review every proposed screenshot and log extract again using the secret-safety rules before sharing the smoke-test result.
