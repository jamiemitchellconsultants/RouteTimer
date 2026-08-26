# RouteTimer Deployment Artifacts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the local and homelab deployment models for RouteTimer's already-built dual-authentication-mode image: Compose projects, run scripts, image publishing, backup/restore, health checks, and public-repository preparation.

**Architecture:** The application code is finished (see `docs/superpowers/plans/2026-08-25-route-timer-auth-modes.md`) — one image, `Auth:Mode` selects Local or Keycloak at runtime. This plan is entirely infrastructure and documentation: two Compose projects sharing one image, run scripts that wrap the local one, a GitHub Actions workflow that tests then publishes multi-arch images to GHCR, backup/restore scripts usable against either Compose project, container health checks that make `docker compose up --wait` mean something, and the artefacts a public repository needs. No application code changes.

**Tech Stack:** Docker Compose, GitHub Actions, bash/PowerShell scripts, Caddy.

**Source spec:** `docs/superpowers/specs/2026-08-25-route-timer-deployment-design.md`, sections 6–13.

---

## Two things this plan corrects that the spec did not anticipate

Both were found by running commands, not by reading further — record them here so a future
compliance check against the spec's literal wording does not revert either fix.

**The runtime image has neither `curl` nor `wget`.** `mcr.microsoft.com/dotnet/aspnet:10.0` is
Ubuntu 24.04 with no HTTP client tool installed. A `HEALTHCHECK CMD curl ...` or `CMD wget ...`
instruction would fail immediately with "command not found" on every check, reporting the
container permanently unhealthy regardless of whether the application is actually up. Task 1
installs `curl` in the runtime stage before adding the `HEALTHCHECK` instruction — verified
against the actual base image, not assumed.

**A container on an `internal: true` Compose network cannot have its published port reached from
the host at all**, even when the container itself also has a `ports:` mapping. Verified directly:
a service on only an internal network refused every connection to its published port; adding a
second, non-internal network to the same service made the identical port mapping work. The local
deployment's `routetimer` service must therefore belong to **two** networks — the internal one
shared with `routetimer-db`, and a second, non-internal one purely so its published port works —
mirroring the shape the homelab Compose project already uses for a different reason (ingress
reachability via `mcp-public`). Task 2 uses this verified shape from the start.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `deploy/docker-compose.local.yml` | Local single-rider Compose project: app + Postgres, loopback-only port, fixed non-secret DB password |
| `run.sh` | macOS/Linux entry point: verify Docker, bring up the local stack, wait for readiness, open the browser |
| `run.ps1` | Windows PowerShell equivalent of `run.sh` |
| `deploy/backup.sh`, `deploy/backup.ps1` | `pg_dump -Fc` to a timestamped file, against either Compose project |
| `deploy/restore.sh`, `deploy/restore.ps1` | `pg_restore --clean` from a dump file, against either Compose project |
| `.github/workflows/publish-image.yml` | Run the full test suite, then build and push multi-arch images to GHCR |
| `LICENSE` | MIT, required before the repository can go public |
| `.github/CODEOWNERS` | Required review gate once the repository is public |
| `RUNBOOK.md` | The rider-facing document: install Docker, run the script, use the app |

**Modified:**

| File | Change |
|---|---|
| `Dockerfile` | Install `curl` in the runtime stage; add a `HEALTHCHECK` instruction probing `/health/ready` |
| `docker-compose.yml` → `deploy/docker-compose.yml` | Moved; `routetimer` pulls a published image by tag instead of building; gains a `healthcheck` block |
| `deploy/caddy/routetimer.caddy` | Literal `routetimer.example.com` placeholder instead of Caddy's own `{$ROUTETIMER_HOSTNAME}` environment substitution, matching the convention LocalAI's substitution script expects |
| `deploy/README.md` | Reflects the moved Compose file, the corrected Caddy placeholder, and points at `RUNBOOK.md` and the backup/restore scripts |

**Deliberately not created by this plan, and not this repository's job:** `LocalAI`'s
`docs/setup-routetimer-windows.ps1` and `docs/deploy-routetimer.md`. Per section 3 of the auth-modes
design spec, this repository owns the artefacts; LocalAI owns deployment execution and is a
separate deliverable in that repository, built against what this plan publishes.

**Deliberately not automated by this plan — require your explicit confirmation when reached (Task
8):** enabling branch protection on `main`, and changing the repository's visibility from private
to public. Both are account/repository settings changes. The plan gives you the exact commands;
an agent executing this plan must present them and wait for your go-ahead rather than run them.

---

## Task 1: Container health checks

**Files:**
- Modify: `Dockerfile`

- [ ] **Step 1: Confirm the runtime image has no HTTP client tool**

Run:

```bash
docker run --rm mcr.microsoft.com/dotnet/aspnet:10.0 sh -c "which curl; which wget"
```

Expected: both commands print nothing (exit non-zero), confirming neither is present. This is why
the next step installs `curl` explicitly rather than assuming it exists.

- [ ] **Step 2: Add curl and the HEALTHCHECK instruction**

In `Dockerfile`, replace the runtime stage:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/api .
COPY --from=build /out/client/wwwroot ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "RouteTimer.Api.dll"]
```

with:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/api .
COPY --from=build /out/client/wwwroot ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=6 \
    CMD curl -f http://localhost:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "RouteTimer.Api.dll"]
```

`/health/ready` is anonymous and already gates on both database reachability and migration
completion (see `docs/superpowers/plans/2026-08-25-route-timer-auth-modes.md` Task 7). `curl -f`
exits non-zero on any HTTP status `>= 400`, which is exactly what `HEALTHCHECK` needs: exit 0 means
healthy, non-zero means not. The 30-second start period covers a cold start doing migrations before
failed checks start counting toward the unhealthy threshold.

- [ ] **Step 3: Build the image**

Run:

```bash
docker build -t routetimer:healthcheck-verify .
```

Expected: build succeeds. This step takes a few minutes (multi-stage: npm vendor assets, dotnet
restore/publish).

- [ ] **Step 4: Verify the HEALTHCHECK is present and correctly reports unhealthy without a database**

Run:

```bash
docker run -d --name routetimer-healthcheck-verify -p 18080:8080 \
    -e Auth__Mode=Keycloak \
    -e Keycloak__Authority=https://kc.invalid/realms/routetimer \
    -e ConnectionStrings__RouteTimer="Host=127.0.0.1;Port=1;Database=nope;Username=x;Password=y;Timeout=1" \
    routetimer:healthcheck-verify
sleep 35
docker inspect --format '{{.State.Health.Status}}' routetimer-healthcheck-verify
```

Expected: `unhealthy`. There is no reachable database, so `/health/ready` correctly reports
unhealthy — this proves the `HEALTHCHECK` instruction is wired to something real, not a check that
always passes regardless of application state. A check that always reports `healthy` would be worse
than no check at all, since it would make `docker compose up --wait` lie.

- [ ] **Step 5: Clean up**

Run:

```bash
docker stop routetimer-healthcheck-verify
docker rm routetimer-healthcheck-verify
docker rmi routetimer:healthcheck-verify
```

The healthy-transition case — a real Postgres, migrations completing, status becoming `healthy` —
is verified end to end in Task 10, once the local Compose project exists to provide one.

- [ ] **Step 6: Commit**

```bash
git add Dockerfile
git commit -m "feat: add a container health check"
```

---

## Task 2: Local deployment Compose project

**Files:**
- Create: `deploy/docker-compose.local.yml`

- [ ] **Step 1: Write the Compose file**

Create `deploy/docker-compose.local.yml`:

```yaml
services:
  routetimer-db:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: routetimer
      POSTGRES_USER: routetimer
      # Not a secret. This is a single-rider install on the rider's own machine: the database
      # publishes no host port and sits on an internal network, so anyone able to use this
      # password can already reach the container directly. A generated per-install password
      # would have to persist alongside the data volume for the volume's whole lifetime, and a
      # rider who lost that file would be permanently locked out of their own training history --
      # a worse outcome than a fixed, documented value for a database nothing external can reach.
      POSTGRES_PASSWORD: routetimer-local-only
    volumes:
      - routetimer_local_postgres:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U routetimer -d routetimer"]
      interval: 5s
      timeout: 5s
      retries: 12
    networks:
      - routetimer-private

  routetimer:
    image: ghcr.io/jamiemitchellconsultants/routetimer:latest
    restart: unless-stopped
    depends_on:
      routetimer-db:
        condition: service_healthy
    environment:
      ConnectionStrings__RouteTimer: Host=routetimer-db;Database=routetimer;Username=routetimer;Password=routetimer-local-only
      Database__ApplyMigrations: "true"
      Auth__Mode: Local
    ports:
      # Loopback only, deliberately. Local mode's whole network story is "only this machine can
      # reach it" -- the passphrase is the gate, and this bind is the wall behind it. Do not
      # simplify this to "49215:8080", which publishes on every interface.
      - "127.0.0.1:${ROUTETIMER_PORT:-49215}:8080"
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/health/ready"]
      interval: 10s
      timeout: 5s
      start_period: 30s
      retries: 6
    networks:
      # routetimer-private reaches the database; routetimer-public exists only so the ports:
      # mapping above actually works -- a container on an internal-only network cannot have its
      # published port reached from the host at all, verified directly while writing this file.
      - routetimer-private
      - routetimer-public

volumes:
  routetimer_local_postgres:

networks:
  routetimer-private:
    internal: true
  routetimer-public: {}
```

`:latest` is deliberate here, not a pinned tag: the run script always `--pull always`s it, so this
is genuinely always the current published image. Immutable SHA/version tags exist for the homelab
model's rollback story (Task 4), which this local, single-rider model does not need.

- [ ] **Step 2: Validate the Compose file syntactically**

Run:

```bash
docker compose -f deploy/docker-compose.local.yml config --quiet
```

Expected: no output, exit code 0. `--quiet` suppresses the rendered config and only reports
errors.

- [ ] **Step 3: Verify the network shape works — the port is reachable, the app can reach the database**

Run:

```bash
docker compose -f deploy/docker-compose.local.yml up -d --pull always --wait
curl -sS -o /dev/null -w "HTTP %{http_code}\n" http://127.0.0.1:49215/health/live
curl -sS -o /dev/null -w "HTTP %{http_code}\n" http://127.0.0.1:49215/health/ready
docker compose -f deploy/docker-compose.local.yml logs routetimer --tail 20
```

Expected: both curls print `HTTP 200`. `--wait` returning at all (rather than timing out) proves
the Compose-level `healthcheck` transitioned to `healthy`, which proves the app reached the real
Postgres and completed migrations — the full chain Task 1 could not verify without a database. The
log tail should show no connection-refused or migration errors.

- [ ] **Step 4: Verify the database published no host port**

Run:

```bash
docker compose -f deploy/docker-compose.local.yml port routetimer-db 5432 2>&1 || echo "no port published, as expected"
```

Expected: the "no port published" message. `docker compose port` errors when a service has no
`ports:` mapping for the requested container port, which is what this Compose file specifies.

- [ ] **Step 5: Tear down**

Run:

```bash
docker compose -f deploy/docker-compose.local.yml down -v
```

`-v` removes the named volume too — this is a throwaway verification run, not data worth keeping.

- [ ] **Step 6: Commit**

```bash
git add deploy/docker-compose.local.yml
git commit -m "feat: add the local deployment Compose project"
```

---

## Task 3: Local run scripts

**Files:**
- Create: `run.sh`
- Create: `run.ps1`

- [ ] **Step 1: Write the macOS/Linux script**

Create `run.sh` at the repository root:

```bash
#!/usr/bin/env bash
set -euo pipefail

PORT="${ROUTETIMER_PORT:-49215}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$ROOT/deploy/docker-compose.local.yml"

if ! command -v docker >/dev/null 2>&1; then
	echo "Docker is not installed. See RUNBOOK.md, Step 1." >&2
	exit 1
fi

if ! docker info >/dev/null 2>&1; then
	echo "Docker is installed but not running. Start Docker Desktop, then run this again." >&2
	exit 1
fi

echo "Starting RouteTimer on port $PORT..."
if ! ROUTETIMER_PORT="$PORT" docker compose -f "$COMPOSE_FILE" up -d --pull always --wait; then
	echo
	echo "Startup failed. If the error above mentions the port already being in use," >&2
	echo "run again with a different port, e.g.:" >&2
	echo "  ROUTETIMER_PORT=49999 ./run.sh" >&2
	echo
	echo "If it instead timed out waiting for the app to become healthy, it may still be" >&2
	echo "applying database migrations on first run -- check:" >&2
	echo "  docker compose -f deploy/docker-compose.local.yml logs -f routetimer" >&2
	exit 1
fi

URL="http://localhost:$PORT"
echo
echo "RouteTimer is running at $URL"

if command -v open >/dev/null 2>&1; then
	open "$URL" 2>/dev/null || true
elif command -v xdg-open >/dev/null 2>&1; then
	xdg-open "$URL" 2>/dev/null || true
else
	echo "Open $URL in your browser."
fi
```

- [ ] **Step 2: Make it executable**

Run:

```bash
chmod +x run.sh
```

- [ ] **Step 3: Write the Windows PowerShell script**

Create `run.ps1` at the repository root:

```powershell
$ErrorActionPreference = "Stop"

$Port = if ($env:ROUTETIMER_PORT) { $env:ROUTETIMER_PORT } else { "49215" }
$ComposeFile = Join-Path $PSScriptRoot "deploy\docker-compose.local.yml"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error "Docker is not installed. See RUNBOOK.md, Step 1."
    exit 1
}

docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker is installed but not running. Start Docker Desktop, then run this again."
    exit 1
}

Write-Host "Starting RouteTimer on port $Port..."
$env:ROUTETIMER_PORT = $Port
docker compose -f $ComposeFile up -d --pull always --wait
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Startup failed. If the error above mentions the port already being in use," -ForegroundColor Yellow
    Write-Host "run again with a different port, e.g.:" -ForegroundColor Yellow
    Write-Host '  $env:ROUTETIMER_PORT="49999"; .\run.ps1' -ForegroundColor Yellow
    Write-Host ""
    Write-Host "If it instead timed out waiting for the app to become healthy, it may still be" -ForegroundColor Yellow
    Write-Host "applying database migrations on first run -- check:" -ForegroundColor Yellow
    Write-Host "  docker compose -f deploy\docker-compose.local.yml logs -f routetimer" -ForegroundColor Yellow
    exit 1
}

$Url = "http://localhost:$Port"
Write-Host ""
Write-Host "RouteTimer is running at $Url"
Start-Process $Url
```

- [ ] **Step 4: Verify run.sh end to end**

Run:

```bash
./run.sh
curl -sS -o /dev/null -w "HTTP %{http_code}\n" "http://localhost:${ROUTETIMER_PORT:-49215}/"
```

Expected: the script prints "RouteTimer is running at http://localhost:49215" and exits 0; the
curl prints `HTTP 200`. A browser should also have opened to that URL.

- [ ] **Step 5: Verify the port-override path**

Run:

```bash
docker compose -f deploy/docker-compose.local.yml down
ROUTETIMER_PORT=49999 ./run.sh
curl -sS -o /dev/null -w "HTTP %{http_code}\n" http://localhost:49999/
docker compose -f deploy/docker-compose.local.yml down -v
```

Expected: RouteTimer comes up on 49999 instead, the curl against that port returns `HTTP 200`, and
the final `down -v` leaves no local-mode containers or volumes behind.

`run.ps1` cannot be run in this environment; a Windows machine (or whoever picks up the LocalAI
integration) is the first real verification of it, following the shape already proven working by
`run.sh` against the identical Compose file.

- [ ] **Step 6: Commit**

```bash
git add run.sh run.ps1
git commit -m "feat: add local deployment run scripts"
```

---

## Task 4: Homelab Compose project — move and pull by tag

**Files:**
- Move: `docker-compose.yml` → `deploy/docker-compose.yml`

- [ ] **Step 1: Move the file and convert it to pull a published image**

Run:

```bash
git mv docker-compose.yml deploy/docker-compose.yml
```

Replace the whole of `deploy/docker-compose.yml` with:

```yaml
services:
  routetimer-db:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: ${ROUTETIMER_DB_NAME:-routetimer}
      POSTGRES_USER: ${ROUTETIMER_DB_USER:-routetimer}
      POSTGRES_PASSWORD: ${ROUTETIMER_DB_PASSWORD:?set ROUTETIMER_DB_PASSWORD}
    volumes:
      - routetimer_postgres:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U $$POSTGRES_USER -d $$POSTGRES_DB"]
      interval: 5s
      timeout: 5s
      retries: 12
    networks:
      - routetimer-private

  routetimer:
    image: ghcr.io/jamiemitchellconsultants/routetimer:${ROUTETIMER_IMAGE_TAG:-latest}
    restart: unless-stopped
    depends_on:
      routetimer-db:
        condition: service_healthy
    environment:
      ConnectionStrings__RouteTimer: Host=routetimer-db;Database=${ROUTETIMER_DB_NAME:-routetimer};Username=${ROUTETIMER_DB_USER:-routetimer};Password=${ROUTETIMER_DB_PASSWORD:?set ROUTETIMER_DB_PASSWORD}
      Database__ApplyMigrations: "true"
      Auth__Mode: Keycloak
      Keycloak__Authority: ${KEYCLOAK_AUTHORITY:?set KEYCLOAK_AUTHORITY}
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/health/ready"]
      interval: 10s
      timeout: 5s
      start_period: 30s
      retries: 6
    networks:
      - routetimer-private
      - mcp-public

volumes:
  routetimer_postgres:

networks:
  routetimer-private:
    internal: true
  mcp-public:
    external: true
    name: mcp-public
```

Two changes from the current file, beyond the move: `routetimer` pulls
`ghcr.io/jamiemitchellconsultants/routetimer:${ROUTETIMER_IMAGE_TAG:-latest}` instead of building
from a local `Dockerfile` — rollback is now redeploying with a different tag rather than checking
out a different commit and rebuilding — and it gained the `healthcheck` block Task 1's `Dockerfile`
change made meaningful.

`mcp-public` needs no `internal: true`/second-network workaround: it is already the
non-internal, external network that gives `routetimer` its ingress reachability, filling the same
role `routetimer-public` fills in the local Compose project.

- [ ] **Step 2: Validate the Compose file syntactically**

Run:

```bash
ROUTETIMER_DB_PASSWORD=verify KEYCLOAK_AUTHORITY=https://kc.invalid/realms/routetimer \
    docker compose -f deploy/docker-compose.yml config --quiet
```

Expected: no output, exit code 0. The two `:?`-guarded variables must be supplied for `config` to
succeed at all — that is the guard doing its job.

- [ ] **Step 3: Verify the guard actually guards**

Run:

```bash
docker compose -f deploy/docker-compose.yml config --quiet 2>&1 || true
```

Expected: an error naming `ROUTETIMER_DB_PASSWORD` (the first `:?` Compose encounters), and a
non-zero exit that the `|| true` swallows so this step doesn't abort the plan. This is the
homelab equivalent of Task 1 Step 4: proving the safeguard is live, not merely present in the
file.

- [ ] **Step 4: Verify the image reference resolves without a tag override**

Run:

```bash
ROUTETIMER_DB_PASSWORD=verify KEYCLOAK_AUTHORITY=https://kc.invalid/realms/routetimer \
    docker compose -f deploy/docker-compose.yml config | grep 'image:'
```

Expected: two `image:` lines — `postgres:16-alpine` and
`ghcr.io/jamiemitchellconsultants/routetimer:latest`, confirming `ROUTETIMER_IMAGE_TAG`'s default
of `latest` resolves correctly when unset.

- [ ] **Step 5: Verify a pinned tag overrides it**

Run:

```bash
ROUTETIMER_DB_PASSWORD=verify KEYCLOAK_AUTHORITY=https://kc.invalid/realms/routetimer \
    ROUTETIMER_IMAGE_TAG=sha-abc1234 \
    docker compose -f deploy/docker-compose.yml config | grep 'image:.*routetimer:'
```

Expected: `image: ghcr.io/jamiemitchellconsultants/routetimer:sha-abc1234`. This is the rollback
mechanism section 9.3 of the spec describes — redeploying is setting this variable to a previous
tag and re-running `docker compose up -d`.

- [ ] **Step 6: Commit**

```bash
git add docker-compose.yml deploy/docker-compose.yml
git commit -m "refactor: move the homelab Compose project and pull by tag"
```

`git mv` stages the deletion and the new file together; both are included in one `git add` for
clarity even though the move already staged them.

---

## Task 5: Caddy placeholder convention and the deploy README

**Files:**
- Modify: `deploy/caddy/routetimer.caddy`
- Modify: `deploy/README.md`

The current `deploy/caddy/routetimer.caddy` uses Caddy's own `{$ROUTETIMER_HOSTNAME}` environment
substitution, resolved from the Caddy process's own environment at parse time. That is a different
mechanism from the one the spec and the established MapToGarmin/LocalAI convention actually use: a
literal placeholder hostname (`maptogarmin.example.com` in that project) that the LocalAI
deployment script substitutes with a plain string replace before writing the file into the shared
ingress's config directory. The `{$VAR}` form would require LocalAI's script to inject
`ROUTETIMER_HOSTNAME` into the *shared Caddy container's own environment* — a different, more
fragile mechanism serving every other site on that ingress, not what any part of this design
describes elsewhere.

- [ ] **Step 1: Switch the Caddy fragment to a literal placeholder**

Replace the whole of `deploy/caddy/routetimer.caddy`:

```caddyfile
# Copy this file to C:\mcp-host\caddy\conf.d\routetimer.caddy, replace routetimer.example.com
# with the real hostname, then validate and reload the shared Caddy ingress.
routetimer.example.com {
    reverse_proxy routetimer:8080
}
```

- [ ] **Step 2: Verify it is valid Caddy configuration**

Run:

```bash
docker run --rm -v "$(pwd)/deploy/caddy/routetimer.caddy:/etc/caddy/Caddyfile:ro" \
    caddy:2-alpine caddy validate --config /etc/caddy/Caddyfile
```

Expected: `Valid configuration`. This validates the fragment in isolation; validating it as part of
the complete shared ingress configuration is LocalAI's job, since that configuration lives in that
repository.

- [ ] **Step 3: Update the deploy README**

Replace the whole of `deploy/README.md`:

```markdown
# RouteTimer deployment

See `RUNBOOK.md` at the repository root for the local, single-rider deployment. This file covers
the homelab deployment, behind an existing shared Caddy ingress.

1. Set `ROUTETIMER_DB_PASSWORD` and `KEYCLOAK_AUTHORITY` (for example
   `https://auth.example.com/realms/routetimer`) in the deployment environment. `Auth__Mode` is
   set to `Keycloak` by `deploy/docker-compose.yml` and must not be removed: the application
   refuses to start without an explicit authentication mode. Neither variable is a build
   argument — the published image is built once and configured entirely at run time.
2. Replace `ROUTETIMER_HOSTNAME` in `keycloak/routetimer-realm.json` and in
   `caddy/routetimer.caddy` with the deployment's real hostname, then import the realm file into
   the existing Keycloak instance. Assign the realm `rider` role to the rider account. Neither
   file's `ROUTETIMER_HOSTNAME` is read from this Compose project's own environment — both are
   plain placeholders substituted by hand here (or by an automated deployment script performing
   the same substitution).
3. Before first deploy, take a backup baseline is not yet meaningful — there is no database yet.
   From the second deploy onward, run `../deploy/backup.sh deploy/docker-compose.yml` first. A
   schema migration has no reverse path; the backup is how you get back to a known state if a
   deploy goes wrong.
4. Set `ROUTETIMER_IMAGE_TAG` to the image tag you intend to run (a commit SHA or version tag
   published by CI; see the repository's GitHub Actions runs). Start with
   `docker compose -f deploy/docker-compose.yml up -d`. The app and database publish no host
   ports; only the app joins `mcp-public`.
5. Copy `caddy/routetimer.caddy` into the shared ingress drop-in directory, validate the complete
   Caddy configuration, then reload it — never restart the shared ingress just for this.
6. Confirm `curl -f https://<hostname>/health/ready` returns `200`.

## Rollback

Redeploy the previous `ROUTETIMER_IMAGE_TAG` and run `docker compose -f deploy/docker-compose.yml
up -d` again. If the previous deploy applied a database migration, an older image is not
guaranteed to work against the newer schema — restore the backup taken in step 3 first.
```

- [ ] **Step 4: Commit**

```bash
git add deploy/caddy/routetimer.caddy deploy/README.md
git commit -m "fix: use a literal hostname placeholder in the Caddy fragment"
```

---

## Task 6: Backup and restore scripts

**Files:**
- Create: `deploy/backup.sh`
- Create: `deploy/backup.ps1`
- Create: `deploy/restore.sh`
- Create: `deploy/restore.ps1`

Both models use the same database service name (`routetimer-db`) and the same default database
name and user (`routetimer`/`routetimer`), so one pair of scripts, parameterised by which Compose
file to target, serves both — exactly what the spec asks for.

- [ ] **Step 1: Write the backup script**

Create `deploy/backup.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 1 ]; then
	echo "Usage: $0 <compose-file> [output-directory]" >&2
	echo "  e.g.: $0 deploy/docker-compose.local.yml" >&2
	exit 1
fi

COMPOSE_FILE="$1"
OUT_DIR="${2:-.}"
DB_USER="${ROUTETIMER_DB_USER:-routetimer}"
DB_NAME="${ROUTETIMER_DB_NAME:-routetimer}"
TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT_FILE="$OUT_DIR/routetimer-$TIMESTAMP.dump"

mkdir -p "$OUT_DIR"
docker compose -f "$COMPOSE_FILE" exec -T routetimer-db \
	pg_dump -Fc -U "$DB_USER" "$DB_NAME" > "$OUT_FILE"

echo "Backup written to $OUT_FILE"
```

- [ ] **Step 2: Write the restore script**

Create `deploy/restore.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 2 ]; then
	echo "Usage: $0 <compose-file> <dump-file>" >&2
	echo "  e.g.: $0 deploy/docker-compose.local.yml routetimer-20260826T090000Z.dump" >&2
	exit 1
fi

COMPOSE_FILE="$1"
DUMP_FILE="$2"
DB_USER="${ROUTETIMER_DB_USER:-routetimer}"
DB_NAME="${ROUTETIMER_DB_NAME:-routetimer}"

if [ ! -f "$DUMP_FILE" ]; then
	echo "Dump file not found: $DUMP_FILE" >&2
	exit 1
fi

docker compose -f "$COMPOSE_FILE" exec -T routetimer-db \
	pg_restore --clean --if-exists -U "$DB_USER" -d "$DB_NAME" < "$DUMP_FILE"

echo "Restored from $DUMP_FILE"
```

- [ ] **Step 3: Make both executable**

```bash
chmod +x deploy/backup.sh deploy/restore.sh
```

- [ ] **Step 4: Write the PowerShell backup script**

Create `deploy/backup.ps1`:

```powershell
param(
    [Parameter(Mandatory = $true)]
    [string] $ComposeFile,

    [string] $OutDir = "."
)

$ErrorActionPreference = "Stop"

$DbUser = if ($env:ROUTETIMER_DB_USER) { $env:ROUTETIMER_DB_USER } else { "routetimer" }
$DbName = if ($env:ROUTETIMER_DB_NAME) { $env:ROUTETIMER_DB_NAME } else { "routetimer" }
$Timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutFile = Join-Path $OutDir "routetimer-$Timestamp.dump"

docker compose -f $ComposeFile exec -T routetimer-db pg_dump -Fc -U $DbUser $DbName |
    Set-Content -Path $OutFile -AsByteStream
if ($LASTEXITCODE -ne 0) {
    Write-Error "pg_dump failed."
    exit 1
}

Write-Host "Backup written to $OutFile"
```

- [ ] **Step 5: Write the PowerShell restore script**

Create `deploy/restore.ps1`:

```powershell
param(
    [Parameter(Mandatory = $true)]
    [string] $ComposeFile,

    [Parameter(Mandatory = $true)]
    [string] $DumpFile
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $DumpFile)) {
    Write-Error "Dump file not found: $DumpFile"
    exit 1
}

$DbUser = if ($env:ROUTETIMER_DB_USER) { $env:ROUTETIMER_DB_USER } else { "routetimer" }
$DbName = if ($env:ROUTETIMER_DB_NAME) { $env:ROUTETIMER_DB_NAME } else { "routetimer" }

Get-Content -Path $DumpFile -AsByteStream -Raw |
    docker compose -f $ComposeFile exec -T routetimer-db pg_restore --clean --if-exists -U $DbUser -d $DbName
if ($LASTEXITCODE -ne 0) {
    Write-Error "pg_restore failed."
    exit 1
}

Write-Host "Restored from $DumpFile"
```

- [ ] **Step 6: Verify a real backup-then-restore round trip against the local stack**

Run:

```bash
docker compose -f deploy/docker-compose.local.yml up -d --pull always --wait

docker compose -f deploy/docker-compose.local.yml exec -T routetimer-db psql -U routetimer -d routetimer -c \
    "INSERT INTO local_credential (\"Id\", \"PasswordHash\", \"CreatedAt\", \"UpdatedAt\") VALUES (1, 'verify-round-trip-hash', now(), now());"

./deploy/backup.sh deploy/docker-compose.local.yml /tmp

DUMP_FILE=$(ls -t /tmp/routetimer-*.dump | head -1)

docker compose -f deploy/docker-compose.local.yml exec -T routetimer-db psql -U routetimer -d routetimer -c \
    "DELETE FROM local_credential;"

./deploy/restore.sh deploy/docker-compose.local.yml "$DUMP_FILE"

docker compose -f deploy/docker-compose.local.yml exec -T routetimer-db psql -U routetimer -d routetimer -t -c \
    "SELECT \"PasswordHash\" FROM local_credential;"

rm "$DUMP_FILE"
docker compose -f deploy/docker-compose.local.yml down -v
```

Expected: the final `SELECT` prints `verify-round-trip-hash`, proving the backup captured the row,
the delete actually removed it, and the restore brought it back. The `local_credential` table is a
convenient target because it is a real table with a `CK_local_credential_singleton` check
constraint (see the auth-modes plan, Task 2) — if restore ever left two rows, or a row with the
wrong id, this specific insert/select pattern would surface it as a constraint violation rather
than silently passing.

- [ ] **Step 7: Commit**

```bash
git add deploy/backup.sh deploy/backup.ps1 deploy/restore.sh deploy/restore.ps1
git commit -m "feat: add backup and restore scripts"
```

---

## Task 7: CI image publishing

**Files:**
- Create: `.github/workflows/publish-image.yml`

- [ ] **Step 1: Write the workflow**

Create `.github/workflows/publish-image.yml`:

```yaml
name: Publish container image

# Runs the full test suite, then -- only if it passes -- builds the multi-stage Dockerfile at the
# repository root and publishes it to GHCR. One image serves both the local and homelab deployment
# models (see docs/superpowers/specs/2026-08-25-route-timer-deployment-design.md), so there is
# only ever one image to build here, not one per model.
on:
  push:
    branches: [main]
    tags: ["v*"]

permissions:
  contents: read
  packages: write

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.302"

      - name: Run the .NET test suite
        run: dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false

      - uses: actions/setup-node@v4
        with:
          node-version: "22"

      - name: Run the route visualization test suite
        working-directory: src/RouteTimer.Client
        run: |
          npm ci
          npm test

  build-and-push:
    needs: test
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: docker/setup-qemu-action@v3

      - uses: docker/setup-buildx-action@v3

      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - uses: docker/metadata-action@v5
        id: meta
        with:
          images: ghcr.io/${{ github.repository_owner }}/routetimer
          tags: |
            type=raw,value=latest,enable={{is_default_branch}}
            type=sha,format=short,prefix=sha-
            type=ref,event=tag

      - uses: docker/build-push-action@v6
        with:
          context: .
          platforms: linux/amd64,linux/arm64
          push: true
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
```

The `test` job needs no Docker-in-Docker setup for the Persistence project's Testcontainers-based
tests: GitHub's `ubuntu-latest` runners have Docker pre-installed and usable directly.
`build-and-push` only runs `needs: test` — a failing test suite stops the publish, which is
acceptance criterion 4 and the entire reason this repository currently has no workflow that could
publish a broken image.

- [ ] **Step 2: Validate the workflow's YAML syntax**

Run:

```bash
python3 -c "import yaml, sys; yaml.safe_load(open('.github/workflows/publish-image.yml')); print('valid YAML')"
```

Expected: `valid YAML`. This only catches syntax errors, not GitHub Actions semantics — the real
verification is the workflow actually running after Step 3's push.

- [ ] **Step 3: Commit and push, then watch it run**

```bash
git add .github/workflows/publish-image.yml
git commit -m "feat: publish multi-arch images to GHCR after tests pass"
git push
```

Then:

```bash
gh run watch
```

Expected: `test` runs and passes, `build-and-push` runs after it and pushes
`ghcr.io/jamiemitchellconsultants/routetimer:latest` and a `sha-<short>` tag. Confirm the image
exists:

```bash
gh api /orgs/jamiemitchellconsultants/packages/container/routetimer/versions --jq '.[0].metadata.container.tags' 2>&1 \
    || gh api /users/jamiemitchellconsultants/packages/container/routetimer/versions --jq '.[0].metadata.container.tags'
```

Expected: a list including `latest` and a `sha-...` tag. Use whichever of the two `gh api` calls
matches whether `jamiemitchellconsultants` is a GitHub organisation or a personal account — try the
`orgs` form first, fall back to `users` if it 404s.

This step pushes to `main` and triggers a real, visible CI run and a real package publish. Confirm
you want this pushed before running it — it is the first commit in this plan that leaves the local
machine.

---

## Task 8: Public repository preparation

**Files:**
- Create: `LICENSE`
- Create: `.github/CODEOWNERS`

- [ ] **Step 1: Add the license**

Create `LICENSE`:

```
MIT License

Copyright (c) 2026 Jamie Mitchell

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

- [ ] **Step 2: Add CODEOWNERS**

Create `.github/CODEOWNERS`:

```
# Every pull request needs an approving review from an owner listed here.
# See RUNBOOK.md for the contributor workflow.

* @jamiemitchellconsultants
```

- [ ] **Step 3: Scan the full history for committed secrets**

Run:

```bash
git log --all -p | grep -inE "(api[_-]?key|secret|password|token|bearer)\s*[:=]\s*['\"a-zA-Z0-9]" \
    | grep -viE "ROUTETIMER_DB_PASSWORD|KEYCLOAK_AUTHORITY|set [A-Z_]+|:\?set|PASSWORD_?HASH|placeholder|example\.com|kc\.invalid|test\.invalid|localhost\.invalid|correct horse|wrong passphrase|hunter2|routetimer-local-only" \
    | head -50
```

Expected: no genuine secret. This is a coarse grep over the whole history, not a purpose-built
scanner — read every line it prints rather than trusting an empty result alone, since it can both
over- and under-match. If it finds nothing to investigate, say so explicitly in the commit message
for this task so the check is a matter of record, not merely something that happened once.

- [ ] **Step 4: Confirm no real hostnames appear outside documented placeholders**

Run:

```bash
grep -rn "tqaentry\.com\|\.local\b" --include="*.md" --include="*.yml" --include="*.yaml" --include="*.caddy" --include="*.json" . \
    | grep -v "\.git/"
```

Expected: no matches, or only matches inside `docs/superpowers/` design documents discussing the
LocalAI host by name in prose (not a deployable artefact). Every deployable file in `deploy/`
should use `routetimer.example.com` or an environment variable, never a real hostname.

- [ ] **Step 5: Present the two remaining manual steps — do not run them without explicit confirmation**

Both change repository-level settings and are not reversible without another explicit action.
Stop here and ask before doing either.

**Enable branch protection on `main`:**

```bash
gh api repos/jamiemitchellconsultants/RouteTimer/branches/main/protection \
    --method PUT \
    --field required_pull_request_reviews[required_approving_review_count]=1 \
    --field enforce_admins=true \
    --field required_status_checks=null \
    --field restrictions=null \
    --field allow_force_pushes=false \
    --field allow_deletions=false
```

**Change the repository's visibility to public:**

```bash
gh repo edit jamiemitchellconsultants/RouteTimer --visibility public --accept-visibility-change-consequences
```

- [ ] **Step 6: Commit**

```bash
git add LICENSE .github/CODEOWNERS
git commit -m "chore: add license and code ownership for public visibility"
```

---

## Task 9: RUNBOOK.md

**Files:**
- Create: `RUNBOOK.md`

- [ ] **Step 1: Write the runbook**

Create `RUNBOOK.md` at the repository root:

```markdown
# Running RouteTimer on your own machine

RouteTimer learns your typical cycling power from Garmin FIT activities you upload, then predicts
how long a route will take you from a GPX file. Everything runs in a small set of Docker
containers on your own machine — nobody else's server ever sees your training data.

Two commands, a few minutes of setup, then a page in your browser.

## What you'll need

- **Docker** — this project runs inside containers, so you don't install .NET, Node, or
  PostgreSQL directly.
- **Git** — the repository is public, so cloning it needs no GitHub account or invite. You'll
  only need an account to contribute a change back; see Contributing, below.

You do **not** need to know .NET, Blazor, or Docker Compose to run this. If you want to read the
design, `docs/superpowers/specs/2026-08-24-route-timer-design.md` and
`docs/superpowers/specs/2026-08-25-route-timer-deployment-design.md` are the design documents.

---

## Step 1 — Install Docker

Pick your OS:

- **macOS:** [Docker Desktop for Mac](https://docs.docker.com/desktop/setup/install/mac-install/)
  — choose Apple Silicon or Intel depending on your Mac.
- **Windows:** [Docker Desktop for Windows](https://docs.docker.com/desktop/setup/install/windows-install/)
  — the installer will prompt to enable WSL2; accept that.
- **Linux:** [Docker Engine](https://docs.docker.com/engine/install/) for your distribution, then
  follow the [linux-postinstall](https://docs.docker.com/engine/install/linux-postinstall/) steps
  so you don't need `sudo` for every command.

**Start Docker Desktop** (macOS/Windows) so the whale icon shows it's running. On Linux,
`sudo systemctl start docker` if it isn't already.

**Verify:**

```bash
docker --version
docker compose version
```

Both should print a version number.

---

## Step 2 — Clone the repository

```bash
git clone https://github.com/jamiemitchellconsultants/RouteTimer.git
cd RouteTimer
```

---

## Step 3 — Run it

**macOS / Linux:**

```bash
./run.sh
```

**Windows (PowerShell):**

```powershell
.\run.ps1
```

This pulls the published container images, starts them, waits until RouteTimer has genuinely
finished starting up — including any database migration, not just "the container is running" —
and opens your browser automatically. Nothing builds on your machine, so this takes seconds even
on the first run, once the images are pulled.

If it doesn't open a browser for you, the script prints the address to open manually — by
default:

```
http://localhost:49215
```

That port is bound to `127.0.0.1` only — nothing on your network can reach it, only your own
machine. If 49215 collides with something else already running, see Troubleshooting.

---

## Step 4 — Set a passphrase

The first time you open RouteTimer, it asks you to choose a passphrase. This protects your
training data — it never leaves your machine, and there is **no way to recover it** if you forget
it. Write it down somewhere safe.

If you do forget it, see Troubleshooting for how to reset it without losing your training data.

---

## Step 5 — Upload training rides and predict a route

1. On the Training page, upload one or more Garmin FIT files from past rides. RouteTimer learns
   how your power output varies with gradient and how long you've been riding.
2. Once you have a few eligible rides, RouteTimer builds a rider model automatically.
3. On the Predictions page, upload a GPX route file. RouteTimer applies your model and a cycling
   physics simulation to estimate your moving time, with a map and elevation/gradient/power/speed
   profiles.

---

## Contributing a change

The repository is public: fork it, make your change, and open a pull request.

```bash
gh repo fork jamiemitchellconsultants/RouteTimer --clone
cd RouteTimer
git checkout -b your-branch-name
# make your change
git push -u origin your-branch-name
gh pr create
```

Every pull request needs an approving review from a repository owner before it can merge —
`.github/CODEOWNERS` lists who that is — and `main` rejects force-pushes and direct pushes.

---

## Everyday commands

**Stop it:**

```bash
docker compose -f deploy/docker-compose.local.yml down
```

**Start it again later:**

```bash
./run.sh          # or .\run.ps1 on Windows
```

This always pulls the current published image before starting, so you always get the latest
version — there's no separate update step.

**See what's running:**

```bash
docker compose -f deploy/docker-compose.local.yml ps
```

**Watch the logs** (useful if something isn't loading):

```bash
docker compose -f deploy/docker-compose.local.yml logs -f
```

---

## Backup and restore

Your training data and predictions live entirely in the `routetimer_local_postgres` Docker
volume. To back it up:

```bash
./deploy/backup.sh deploy/docker-compose.local.yml
```

This writes a timestamped dump file to the current directory. To restore from one:

```bash
./deploy/restore.sh deploy/docker-compose.local.yml routetimer-20260826T090000Z.dump
```

Restoring replaces everything currently in the database with the dump's contents.

---

## Troubleshooting

**"Docker is not installed" / "Docker is not running"** — Install or start Docker Desktop
(Step 1), then run the script again.

**Port 49215 is already in use on your machine** — Run with a different port for this session:

```bash
ROUTETIMER_PORT=49999 ./run.sh
```

```powershell
$env:ROUTETIMER_PORT="49999"; .\run.ps1
```

**Startup times out waiting for RouteTimer to become healthy** — On first run this can mean
database migrations are still applying. Check:

```bash
docker compose -f deploy/docker-compose.local.yml logs -f routetimer
```

**You forgot your passphrase** — Your training data is not lost. Clear the stored credential and
set a new passphrase:

```bash
docker compose -f deploy/docker-compose.local.yml exec routetimer-db \
    psql -U routetimer -d routetimer -c "DELETE FROM local_credential;"
```

The next time you open RouteTimer, it asks you to set a passphrase again, exactly as on first
run. Your uploaded activities, model, and predictions are untouched — only the passphrase is
cleared.

**Starting over completely** (deletes all your training data and predictions):

```bash
docker compose -f deploy/docker-compose.local.yml down -v
./run.sh
```

---

## What's actually running

Two containers: RouteTimer itself (an ASP.NET Core API serving a compiled Blazor WebAssembly
client from the same origin) and PostgreSQL, holding your uploaded activities, rider model, and
prediction history. The database publishes no port of its own — only RouteTimer's own port,
bound to `127.0.0.1`, is reachable at all, and only from your own machine.

The image is built automatically by
[a GitHub Actions workflow](.github/workflows/publish-image.yml) on every change to `main`, for
both Intel/AMD and Apple Silicon/ARM machines, and published to
[GitHub Container Registry](https://github.com/jamiemitchellconsultants/RouteTimer/pkgs/container/routetimer)
— so `./run.sh` never builds anything on your computer. `deploy/docker-compose.local.yml` is the
file that defines what runs, if you're curious, or if you'd rather build it yourself:

```bash
docker compose -f deploy/docker-compose.local.yml build
./run.sh
```

This replaces your local copy of the `:latest` tag with your own build, but only until the next
time you run `./run.sh`, which always re-pulls the published image and overwrites it again.
```

- [ ] **Step 2: Verify the runbook's commands actually work end to end**

Run:

```bash
docker compose -f deploy/docker-compose.local.yml down -v 2>/dev/null || true
./run.sh
docker compose -f deploy/docker-compose.local.yml exec -T routetimer-db \
    psql -U routetimer -d routetimer -c "SELECT 1;" >/dev/null
echo "database reachable"
docker compose -f deploy/docker-compose.local.yml down
./run.sh
curl -sS -o /dev/null -w "HTTP %{http_code}\n" "http://localhost:${ROUTETIMER_PORT:-49215}/"
docker compose -f deploy/docker-compose.local.yml exec routetimer-db \
    psql -U routetimer -d routetimer -c "DELETE FROM local_credential;"
docker compose -f deploy/docker-compose.local.yml down -v
```

Expected: `run.sh` succeeds both times (the second proves "start it again later" genuinely reuses
existing state rather than requiring `-v` every time), `database reachable` prints, the curl
after the second start prints `HTTP 200`, and the passphrase-reset `DELETE` runs without error.

- [ ] **Step 3: Commit**

```bash
git add RUNBOOK.md
git commit -m "docs: add the RouteTimer runbook"
```

---

## Task 10: End-to-end verification

No new files — this task runs the acceptance criteria from
`docs/superpowers/specs/2026-08-25-route-timer-deployment-design.md` section 13 against everything
this plan built, together.

- [ ] **Step 1: Validate both Compose files**

```bash
docker compose -f deploy/docker-compose.local.yml config --quiet
ROUTETIMER_DB_PASSWORD=verify KEYCLOAK_AUTHORITY=https://kc.invalid/realms/routetimer \
    docker compose -f deploy/docker-compose.yml config --quiet
```

Expected: both silent, exit 0.

- [ ] **Step 2: Validate the Caddy fragment as part of a complete ingress configuration**

```bash
mkdir -p /tmp/routetimer-caddy-verify
cat > /tmp/routetimer-caddy-verify/Caddyfile <<'EOF'
{
	email test@example.com
}

import conf.d/*.caddy
EOF
mkdir -p /tmp/routetimer-caddy-verify/conf.d
cp deploy/caddy/routetimer.caddy /tmp/routetimer-caddy-verify/conf.d/
docker run --rm -v /tmp/routetimer-caddy-verify:/etc/caddy:ro \
    caddy:2-alpine caddy validate --config /etc/caddy/Caddyfile
rm -rf /tmp/routetimer-caddy-verify
```

Expected: `Valid configuration`. This validates the fragment the way the shared ingress actually
loads it — via `import`, inside a real global block — not in isolation as Task 5 did.

- [ ] **Step 3: Run the full local smoke test from a clean slate**

```bash
docker compose -f deploy/docker-compose.local.yml down -v 2>/dev/null || true
docker volume rm routetimer-step10-deployment-artifacts_routetimer_local_postgres 2>/dev/null || true
./run.sh
docker compose -f deploy/docker-compose.local.yml ps
```

Expected: `run.sh` succeeds and both services show `healthy` in `docker compose ... ps`.

- [ ] **Step 4: Confirm first-run setup, then a real prediction, through the actual HTTP API**

```bash
BASE="http://localhost:${ROUTETIMER_PORT:-49215}"

curl -sS "$BASE/api/auth/config" | grep -q '"setupRequired":true' && echo "setup required, as expected"

curl -sS -c /tmp/routetimer-verify-cookies.txt -X POST "$BASE/api/auth/setup" \
    -H "Content-Type: application/json" \
    -d '{"passphrase":"verify this deployment works"}' \
    -o /dev/null -w "setup: HTTP %{http_code}\n"

curl -sS -b /tmp/routetimer-verify-cookies.txt "$BASE/api/auth/session" | grep -q '"authenticated":true' \
    && echo "session authenticated after setup, as expected"

curl -sS -b /tmp/routetimer-verify-cookies.txt "$BASE/api/profile" -o /dev/null -w "profile: HTTP %{http_code}\n"

rm /tmp/routetimer-verify-cookies.txt
```

Expected: `setup required, as expected`; `setup: HTTP 200`; `session authenticated after setup, as
expected`; `profile: HTTP 404` (no profile has been set yet — 404 rather than 401 is exactly what
proves authentication succeeded, since an unauthenticated request to that endpoint is 401). This
exercises the real deployed image end to end: HTTP, cookie auth, the database, all through the
Compose network — not a unit test standing in for it.

An actual FIT upload and GPX prediction needs real ride data this environment does not have;
`docs/superpowers/specs/2026-08-25-route-timer-deployment-design.md` section 12 already documents
that as a manual step for a human running `RUNBOOK.md` Steps 4–5 themselves.

- [ ] **Step 5: Verify readiness stays unhealthy while migrations are incomplete**

```bash
docker compose -f deploy/docker-compose.local.yml exec -T routetimer-db \
    psql -U routetimer -d routetimer -c "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = (SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1);"
docker compose -f deploy/docker-compose.local.yml restart routetimer
sleep 5
docker inspect --format '{{.State.Health.Status}}' $(docker compose -f deploy/docker-compose.local.yml ps -q routetimer)
sleep 30
docker inspect --format '{{.State.Health.Status}}' $(docker compose -f deploy/docker-compose.local.yml ps -q routetimer)
```

Expected: the first health status check may print `starting` or `unhealthy`; by the second check,
`healthy` — `DatabaseMigrationService` re-applies the deleted migration record on restart (EF Core
migrations are idempotent against a stale history row) and readiness recovers once that completes.
This is a deliberately destructive-looking but fully recoverable check against a throwaway
Compose volume — do not run it against a deployment holding real data.

- [ ] **Step 6: Backup/restore round trip, once more, against this clean-slate instance**

```bash
./deploy/backup.sh deploy/docker-compose.local.yml /tmp
DUMP_FILE=$(ls -t /tmp/routetimer-*.dump | head -1)
docker compose -f deploy/docker-compose.local.yml down -v
docker compose -f deploy/docker-compose.local.yml up -d --pull always --wait
./deploy/restore.sh deploy/docker-compose.local.yml "$DUMP_FILE"
curl -sS "http://localhost:${ROUTETIMER_PORT:-49215}/api/auth/config" | grep -q '"setupRequired":false' \
    && echo "restore recovered the passphrase set in step 4, as expected"
rm "$DUMP_FILE"
```

Expected: the final grep succeeds — proving restore against a completely fresh volume (not just a
truncated table, as Task 6's verification used) recovers a real prior deployment's state.

- [ ] **Step 7: Final teardown**

```bash
docker compose -f deploy/docker-compose.local.yml down -v
```

- [ ] **Step 8: Confirm every acceptance criterion**

Read `docs/superpowers/specs/2026-08-25-route-timer-deployment-design.md` section 13 and confirm
each of the 9 criteria against what this plan built. All should now be satisfied except the parts
explicitly and permanently out of this repository's scope: a live homelab deployment behind a real
shared ingress (needs that ingress, elsewhere), and LocalAI's own `setup-routetimer-windows.ps1` /
`deploy-routetimer.md` (a separate deliverable in that repository).

- [ ] **Step 9: No commit**

This task changes no files — it is verification only. If any step above fails, fix the specific
task that owns the broken behaviour and re-run this task's steps from the top, rather than patching
around the symptom here.

---

## Plan Complete

Both deployment models are documented, scripted, and verified against the real Docker network,
the real image, and the real database. CI publishes the image only after the test suite passes.
Backup and restore are proven round-trip against a running instance, twice, from two different
starting states. The repository has what public visibility requires except the two account-level
changes Task 8 deliberately left for your explicit go-ahead.

Not built here, by design: LocalAI's `setup-routetimer-windows.ps1` and `deploy-routetimer.md` —
a separate deliverable in that repository, against the artefacts this plan publishes.
