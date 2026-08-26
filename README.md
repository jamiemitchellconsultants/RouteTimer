# RouteTimer

RouteTimer learns your cycling power from past rides, then predicts how long a new route will take
you — moving time, speed, and power, segment by segment along the elevation profile — before you
ride it.

## Why this exists

Most route-time estimators use a flat average speed. RouteTimer instead builds a personal power
model from your own ride history — how your power output varies with gradient and how long you've
been riding — then runs a cycling physics simulation over the route's actual elevation profile.
Two riders on the same route get different predictions if their training history says they should.

Training data and predictions stay on infrastructure you control: your own machine for a single
rider, or your own shared deployment for a few. Nobody else's server ever sees your rides.

## How it works

A .NET 10 solution: an ASP.NET Core API, a Blazor WebAssembly client served from the same origin,
PostgreSQL for training history and predictions, and a small internal Python adapter that talks to
Garmin Connect on the API's behalf — the only component with a path out to Garmin, on its own
network segment. Route physics, rider modelling, and prediction all run server-side; the client is
presentation only.

## Using it

1. **Add training rides.** On the Training page, upload FIT files from past rides directly, or
   connect a Garmin Connect account and import road/gravel activities from there instead. A few
   eligible rides is enough for RouteTimer to build a rider model automatically.
2. **Predict a route.** On the Predictions page, either upload a GPX file or paste a Google Maps
   route link — RouteTimer builds the GPX in your browser from Google's own directions and
   elevation data, using your own Google Maps API key (savable, encrypted at rest, or typed fresh
   each time). Either path applies your rider model and the physics simulation to estimate moving
   time, with a map and elevation/gradient/power/speed profiles.
3. **Take the result with you.** Download a completed prediction as a GPX file — plain, or with
   predicted times stamped on each point — or send it directly to Garmin Connect as a course if you
   have a Garmin account connected.

Connecting Garmin is optional throughout; everything above works from manually uploaded FIT and GPX
files alone.

## Running it

**→ [RUNBOOK.md](RUNBOOK.md)** is the self-hosting guide: install Docker, clone the repo, two
commands, a page in your browser. No prior familiarity with .NET, Blazor, Python, or Docker Compose
required.

**Deploying it for others.** RUNBOOK.md covers running RouteTimer on your own machine for yourself.
Hosting it behind a shared ingress for other people to reach — with real authentication rather than
a single local passphrase — is a different setup: see [deploy/README.md](deploy/README.md).

## Design and planning documents

Every feature was designed and planned in writing before being built, under
`docs/superpowers/`:

- `docs/superpowers/specs/` — approved design specs, one per feature, in chronological order:
  the core route-timer design, deployment, Garmin activity import, Step 9 API/UI, and the
  Google Maps route builder / GPX export / Garmin course push.
- `docs/superpowers/plans/` — the implementation plan derived from each spec, broken into
  reviewable, test-driven tasks.

Start with the most recent design doc in each directory for the fullest picture of the current
system; earlier ones capture decisions made along the way. `docs/garmin-smoke-test.md` and
`docs/garmin-course-spike.md` record the manual, opt-in verification procedures against a real
Garmin account that this project's unofficial Garmin integration depends on and that stay
deliberately outside CI.

## License

[MIT](LICENSE)
