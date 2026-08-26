# Running RouteTimer on your own machine

RouteTimer learns your typical cycling power from past rides, then predicts how long a route will
take you from a GPX file. Everything runs in a small set of Docker containers on your own
machine — nobody else's server ever sees your training data.

Two commands, a few minutes of setup, then a page in your browser.

## What you'll need

- **Docker** — this project runs inside containers, so you don't install .NET, Node, Python, or
  PostgreSQL directly.
- **Git** — the repository is public, so cloning it needs no GitHub account or invite. You'll
  only need an account to contribute a change back; see Contributing, below.
- **openssl** — used once, on first run, to generate a local encryption key. It ships with macOS
  and almost every Linux distribution already; Windows users get it for free via Git for Windows,
  which `run.ps1` also uses under the hood.

You do **not** need to know .NET, Blazor, Python, or Docker Compose to run this. If you want to
read the design, `docs/superpowers/specs/2026-08-24-route-timer-design.md`,
`docs/superpowers/specs/2026-08-25-route-timer-deployment-design.md`, and
`docs/superpowers/specs/2026-08-25-garmin-activity-import-design.md` are the design documents.

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

The first time you run this, it generates a random encryption key that protects any Garmin
account you later connect, and saves it to `deploy/.env.local` — a file that never leaves your
machine and is excluded from git. Then it pulls the published container images, starts them,
waits until RouteTimer has genuinely finished starting up — including any database migration, not
just "the containers are running" — and opens your browser automatically. Nothing builds on your
machine, so this takes seconds even on the first run, once the images are pulled.

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

## Step 5 — Add training rides and predict a route

1. On the Training page, either upload Garmin FIT files from past rides directly, or connect your
   Garmin Connect account and import road/gravel activities from there instead — both feed the
   same training workflow. RouteTimer learns how your power output varies with gradient and how
   long you've been riding.
2. Once you have a few eligible rides, RouteTimer builds a rider model automatically.
3. On the Predictions page, upload a GPX route file. RouteTimer applies your model and a cycling
   physics simulation to estimate your moving time, with a map and elevation/gradient/power/speed
   profiles.

Connecting Garmin is optional. Everything above works from manually uploaded FIT files alone.

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
docker compose -f deploy/docker-compose.local.yml --env-file deploy/.env.local down
```

**Start it again later:**

```bash
./run.sh          # or .\run.ps1 on Windows
```

This always pulls the current published images before starting, so you always get the latest
version — there's no separate update step.

**See what's running:**

```bash
docker compose -f deploy/docker-compose.local.yml --env-file deploy/.env.local ps
```

**Watch the logs** (useful if something isn't loading):

```bash
docker compose -f deploy/docker-compose.local.yml --env-file deploy/.env.local logs -f
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

Restoring replaces everything currently in the database with the dump's contents. It does not
touch `deploy/.env.local` — your Garmin encryption key is unaffected, so a restored connection to
Garmin (if you had one) stays usable.

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
docker compose -f deploy/docker-compose.local.yml --env-file deploy/.env.local logs -f routetimer
```

**You forgot your passphrase** — Your training data is not lost. Clear the stored credential and
set a new passphrase:

```bash
docker compose -f deploy/docker-compose.local.yml --env-file deploy/.env.local exec routetimer-db \
    psql -U routetimer -d routetimer -c "DELETE FROM local_credential;"
```

The next time you open RouteTimer, it asks you to set a passphrase again, exactly as on first
run. Your uploaded activities, model, predictions, and any connected Garmin account are untouched
— only the passphrase is cleared.

**You lost `deploy/.env.local`, or it's been deleted** — RouteTimer will refuse to start (its
Garmin encryption key is gone). Delete the file if it partially exists, then run `./run.sh` again
to generate a fresh one. Any previously connected Garmin account will show as needing
reconnection, since its stored tokens can no longer be decrypted; your training data, model, and
predictions are unaffected.

**Starting over completely** (deletes all your training data and predictions, but keeps your
Garmin encryption key so a reconnect isn't required):

```bash
docker compose -f deploy/docker-compose.local.yml --env-file deploy/.env.local down -v
./run.sh
```

---

## What's actually running

Three containers: RouteTimer itself (an ASP.NET Core API serving a compiled Blazor WebAssembly
client from the same origin), PostgreSQL, holding your uploaded activities, rider model, and
prediction history, and a small internal Garmin Connect adapter that RouteTimer talks to only over
the Docker network — it has no port reachable from your machine, and it never touches your
database or your local passphrase. Only RouteTimer's own port, bound to `127.0.0.1`, is reachable
at all, and only from your own machine.

Both images are built automatically by
[a GitHub Actions workflow](.github/workflows/publish-image.yml) on every change to `main`, for
both Intel/AMD and Apple Silicon/ARM machines, and published to
[GitHub Container Registry](https://github.com/jamiemitchellconsultants/RouteTimer/pkgs/container/routetimer)
— so `./run.sh` never builds anything on your computer. `deploy/docker-compose.local.yml` is the
file that defines what runs, if you're curious, or if you'd rather build it yourself:

```bash
docker compose -f deploy/docker-compose.local.yml build
./run.sh
```

This replaces your local copy of the `:latest` tags with your own build, but only until the next
time you run `./run.sh`, which always re-pulls the published images and overwrites them again.
