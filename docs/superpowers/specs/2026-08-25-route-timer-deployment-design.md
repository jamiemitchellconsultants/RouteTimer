# RouteTimer Deployment Design

**Date:** 2026-08-25
**Status:** Approved in design review; awaiting review of this written specification
**Supersedes:** section 16 of `2026-08-24-route-timer-design.md` where the two documents differ

## 1. Purpose

RouteTimer has two deployment models, and the first release must serve both from one
published artefact:

1. **Local mode.** A rider runs the whole application on their own machine under Docker
   Desktop. Setup is streamlined to the standard set by the MapToGarmin runbook: install
   Docker, clone, run one script. The rider sets a credential on first use.
2. **Homelab mode.** The application is deployed to the `ai-mcp-server` host behind the
   existing shared Caddy ingress, authenticating through the Keycloak realm the original
   design specifies.

The original design assumed homelab deployment only, and baked deployment-specific values
into the container image at build time. That assumption is incompatible with publishing a
single image that local riders can run, and this document replaces it.

## 2. Confirmed Scope

This work shall:

- make the authentication mode an explicit deployment setting with two implementations;
- move the client's authentication configuration from build time to runtime;
- add local first-run credential setup, login, logout, and login rate limiting;
- add a Compose project and run scripts for local mode;
- convert the homelab Compose project to pull a published image by tag;
- publish a multi-arch image to GHCR from CI, gated on the test suite;
- add backup and restore scripts and document backup, migration, and rollback procedures;
- add a migration-state readiness check plus container and Compose health checks; and
- prepare the repository for public visibility.

This work shall not add multi-user support, rider identity partitioning, stored third-party
API keys, scheduled automatic backups, or any change to prediction, model, or training
behaviour.

## 3. Repository Ownership

RouteTimer owns its deployment artefacts. LocalAI owns deployment execution. This follows
the precedent already set by MapToGarmin:

- **This repository** provides `deploy/docker-compose.yml`, `deploy/docker-compose.local.yml`,
  `deploy/caddy/routetimer.caddy`, `deploy/keycloak/routetimer-realm.json`, the backup and
  restore scripts, `run.sh`, `run.ps1`, `RUNBOOK.md`, and `deploy/README.md` documenting the
  manual homelab procedure so the repository stands alone.
- **The LocalAI repository** provides `docs/setup-routetimer-windows.ps1` and
  `docs/deploy-routetimer.md`, following the shape of the existing MapToGarmin equivalents.
  That script never hand-writes RouteTimer's Caddy site block; it substitutes the hostname
  placeholder into the artefact this repository ships, so an upstream change is picked up on
  the next run rather than drifting from a pasted copy.

## 4. Authentication Modes

### 4.1 Mode selection

`Auth:Mode` is a required setting with no default. Permitted values are `Local` and
`Keycloak`. An unset or unrecognised value fails startup with an explicit message naming the
setting and its permitted values.

There is deliberately no fallback in either direction. A deployment that does not state what
it is does not start. This means an existing homelab deployment will not start until the
variable is added, which is intended and must be called out in the deploy documentation.

### 4.2 One authorization policy, two authentication schemes

The existing fallback authorization policy — an authenticated user holding the `rider` role —
is unchanged, and no endpoint changes. The two modes differ only in how a principal carrying
that role is produced:

- `Keycloak` mode registers JWT bearer against the configured authority with the
  `routetimer-api` audience, exactly as today, including the existing realm-role mapping.
- `Local` mode registers a cookie authentication scheme. A successfully authenticated local
  session carries the `rider` role claim.

Nothing downstream of the authorization policy is aware of the mode. The second path is an
authentication adapter, not a parallel authorization model.

### 4.3 Local credential storage

A single-row `local_credential` table stores a password hash produced by the framework's
`PasswordHasher<T>` (PBKDF2), with created and updated timestamps. No new dependency is
introduced. The table holds at most one row; a second insert is a programming error, not a
supported state.

This table is the intended location for per-rider secrets in a future release, such as a
saved third-party API key. No such storage is built now.

### 4.4 Local endpoints

Three anonymous endpoints, present only in `Local` mode:

- `POST /api/auth/setup` — sets the initial passphrase. Succeeds only while no credential
  exists; returns a problem response once one does.
- `POST /api/auth/login` — validates the passphrase and issues the session cookie. Rate
  limited with a fixed-window limiter and lockout after repeated failures.
- `POST /api/auth/logout` — clears the session cookie.

The session cookie is `HttpOnly` and `SameSite=Strict`, and is marked `Secure` when the
request arrives over HTTPS. Local mode over plain HTTP on loopback is expected and supported;
marking the cookie `Secure` unconditionally would break it.

### 4.5 Credential recovery

A forgotten passphrase is recovered by deleting the credential row through
`docker compose exec` against the database container. The next page load presents first-run
setup again. Training data is untouched.

This is documented in `RUNBOOK.md` and requires no application code. A recovery code
mechanism was considered and rejected: it introduces a second secret to store and explain,
covering a case the documented command already handles.

## 5. Runtime Authentication Configuration

### 5.1 The problem with the current build

The Dockerfile currently requires `KEYCLOAK_AUTHORITY` and `ROUTETIMER_HOSTNAME` build
arguments and writes them into a generated `appsettings.Production.json` consumed by the
WebAssembly client. A single published image cannot carry one deployment's Keycloak authority,
so this must move to runtime.

### 5.2 The replacement

`GET /api/auth/config` is anonymous and returns:

- the authentication mode;
- whether first-run setup is still required, in `Local` mode; and
- the authority, client id, redirect URI, and post-logout redirect URI, in `Keycloak` mode.

The client fetches this before `builder.Build()` and registers accordingly:

- `Keycloak` mode registers `AddOidcAuthentication` and the `AuthorizationMessageHandler`
  wrapped `HttpClient`, as today.
- `Local` mode registers a cookie-backed `AuthenticationStateProvider` and a plain
  same-origin `HttpClient`; the browser attaches the session cookie automatically and no
  bearer handler is involved.

### 5.3 Consequences

Three simplifications follow, all of them intended:

- the `KEYCLOAK_AUTHORITY` and `ROUTETIMER_HOSTNAME` build arguments are removed;
- the generated `appsettings.Production.json` is removed; and
- the homelab deployment no longer rebuilds the image to change its public hostname, so the
  artefact local riders run and the artefact the homelab runs are the same bytes.

The static `Keycloak` section in `wwwroot/appsettings.json` is removed. `MapTiles` remains a
static client setting.

## 6. Local Deployment Model

`deploy/docker-compose.local.yml` defines two services and no Keycloak:

- `routetimer`, from the published GHCR image, with `Auth__Mode=Local`; and
- `routetimer-db`, `postgres:16-alpine`, on an internal network with a named volume.

### 6.1 Network exposure

The web service publishes `127.0.0.1:${ROUTETIMER_PORT:-49215}:8080`. The loopback prefix is
load-bearing and must not be simplified to `"49215:8080"`, which publishes on every interface.
For an application holding a rider's training history, the passphrase is the gate and the
loopback bind is the wall.

The database publishes no host port.

### 6.2 Database credentials

Local mode uses a fixed, documented database password. It is not treated as a secret, and the
documentation says so plainly.

A generated per-install password was considered and rejected. It must persist alongside the
volume for the lifetime of the data, and a rider who loses that file is permanently locked out
of their own training history. Given the database publishes no host port and sits on an
internal network, anyone able to use that password can already reach the container directly.
A generated value would perform security rather than add it.

### 6.3 Run scripts

`run.sh` and `run.ps1` at the repository root mirror MapToGarmin's:

1. verify Docker is installed and running, with actionable messages if not;
2. `docker compose -f deploy/docker-compose.local.yml up -d --pull always --wait`;
3. print the URL and open a browser;
4. on failure, suggest the port override.

The port is overridden with the `ROUTETIMER_PORT` environment variable.

### 6.4 First run

The browser opens to first-run setup because `GET /api/auth/config` reports that setup is
required. The rider sets a passphrase, is signed in, and reaches the dashboard.

## 7. Homelab Deployment Model

`deploy/docker-compose.yml` — moved from the repository root so both models sit together —
pulls `ghcr.io/jamiemitchellconsultants/routetimer:${ROUTETIMER_IMAGE_TAG}` rather than
building. It retains the external `mcp-public` network and the internal `routetimer-private`
network, publishes no host ports, and sets `Auth__Mode=Keycloak`.

Deployment inputs remain the public hostname, the Keycloak authority, and the database
password. They are now environment values only; none is a build argument.

`deploy/caddy/routetimer.caddy` uses the placeholder hostname `routetimer.example.com`,
matching the convention the LocalAI substitution step expects.

Rollback is pinning `ROUTETIMER_IMAGE_TAG` to the previously deployed tag and re-running. See
section 9 for the database constraint on this.

## 8. Image Publishing

A single GitHub Actions workflow runs on pushes to `main` and on version tags:

1. run the full test suite;
2. build multi-arch images for `linux/amd64` and `linux/arm64`; and
3. push to GHCR tagged `latest`, the commit SHA, and the semantic version on tag builds.

Tests gate the publish. The repository currently has no workflows at all, so nothing today
prevents publishing a broken image.

The immutable SHA and version tags are what make homelab rollback meaningful; `latest` exists
for the local run scripts, which always pull it.

## 9. Backup, Migration, and Rollback

### 9.1 Scripts

`deploy/backup.sh`, `deploy/backup.ps1`, `deploy/restore.sh`, and `deploy/restore.ps1` wrap
`pg_dump -Fc` to a timestamped file and `pg_restore --clean`. Each takes the Compose file as a
parameter, so the same scripts serve both deployment models.

### 9.2 Migrations

Migrations are applied on startup when `Database__ApplyMigrations` is true.
`DatabaseMigrationService` already holds a PostgreSQL advisory lock across `MigrateAsync`, so
concurrent instances cannot migrate simultaneously.

Readiness does not currently wait for them, and this work must fix that. The migration service
is registered after the web host's own hosted service, so Kestrel begins listening before
migrations run, and the existing readiness check proves only that the database is reachable —
not that the schema is current. Between those two points `/health/ready` can report healthy
against a database that is still migrating.

A readiness check reporting migration state is therefore added and tagged `ready`, so
`/health/ready` fails until migrations have completed successfully. Without it, Compose's
`--wait` and the homelab deployment's readiness gate both return early, which is the specific
promise the run scripts make to the rider.

### 9.3 Rollback

Rollback of the application is redeploying the previous image tag. Rollback across a schema
migration is restoring the pre-deployment dump.

Migrations are forward-only in practice: the deployment path applies them automatically and
provides no reverse path. An older image meeting a newer schema is not a supported state. This
is precisely why taking a backup is a required step of the deployment procedure rather than a
recommendation, and the documentation states it in those terms.

## 10. Health Checks

`/health/live` and `/health/ready` already exist, are anonymous, and expose no detail.
`/health/ready` is currently gated on a database-reachability check only.

Three things are missing and are added here:

- a migration-state readiness check, tagged `ready`, as described in section 9.2;
- a `HEALTHCHECK` instruction in the Dockerfile probing `/health/ready`; and
- a `healthcheck` on the web service in both Compose files.

Without them `docker compose up --wait` has nothing to wait on, and the run scripts cannot
honour the runbook's promise to wait until the application is genuinely ready to serve rather
than merely started.

## 11. Public Repository Preparation

The repository changes from private to public. Before that happens:

- scan the full history for committed secrets;
- confirm no real hostnames appear outside documented placeholders;
- add a LICENSE;
- add `.github/CODEOWNERS`; and
- enable branch protection on `main` requiring review and rejecting force pushes.

No rider FIT or GPX files are committed, and that constraint is unchanged.

## 12. Verification Strategy

Automated tests cover:

- mode selection, including startup failure on an unset or invalid `Auth:Mode`;
- `GET /api/auth/config` response shape in both modes;
- first-run setup succeeding once and refusing to run again;
- login success, failure, and lockout after repeated failures; and
- anonymous rejection of protected endpoints in both modes.

Deployment verification covers `docker compose config` validation of both Compose files,
`caddy validate` of the complete ingress configuration including the RouteTimer fragment,
container health check transitions, readiness remaining unhealthy while migrations are
incomplete, and a backup-then-restore round trip.

A documented manual smoke test completes local acceptance: run the script, set a passphrase,
upload a FIT file, and produce a prediction from a GPX route.

## 13. Acceptance Criteria

This work is complete when:

1. one published image runs correctly in both authentication modes;
2. `run.sh` and `run.ps1` bring up a working local instance that opens to first-run setup;
3. the homelab Compose project deploys a pinned image tag behind the shared ingress;
4. CI publishes multi-arch images to GHCR only after the test suite passes;
5. backup and restore round-trip successfully in both models;
6. backup, migration, and rollback procedures are documented;
7. readiness reports healthy only after migrations complete, and health checks gate
   startup in both Compose files;
8. `RUNBOOK.md` takes a rider from nothing to a prediction; and
9. the repository is ready for public visibility.

LocalAI's `setup-routetimer-windows.ps1` and `deploy-routetimer.md` are a separate deliverable
in that repository, built against the artefacts this one publishes.
