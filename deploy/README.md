# RouteTimer deployment

1. Set `ROUTETIMER_DB_PASSWORD`, `KEYCLOAK_AUTHORITY` (for example `https://auth.example.com/realms/routetimer`), and `ROUTETIMER_HOSTNAME` in the deployment environment. `Auth__Mode` is set to `Keycloak` by the Compose file and must not be removed: the application refuses to start without an explicit authentication mode. None of these is a build argument any more — the image is built once and configured at run time.
2. Import `keycloak/routetimer-realm.json` into the existing Keycloak instance, replacing `ROUTETIMER_HOSTNAME` first. Assign the realm `rider` role to the rider account.
3. Start with `docker compose up -d --build`. The app and database publish no host ports; only the app joins `mcp-public`.
4. Copy `caddy/routetimer.caddy` into the shared ingress drop-in directory, validate Caddy, then reload it.
