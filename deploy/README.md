# RouteTimer deployment

1. Set `ROUTETIMER_DB_PASSWORD` and `KEYCLOAK_AUTHORITY` (for example `https://auth.example.com/realms/routetimer`) in the deployment environment. `Auth__Mode` is set to `Keycloak` by the Compose file and must not be removed: the application refuses to start without an explicit authentication mode. Neither is a build argument any more — the image is built once and configured at run time.
2. Replace `ROUTETIMER_HOSTNAME` in `keycloak/routetimer-realm.json` with the deployment's real hostname, then import the file into the existing Keycloak instance. Assign the realm `rider` role to the rider account. This Compose project does not read `ROUTETIMER_HOSTNAME` itself — it is only a placeholder in the realm file and in `caddy/routetimer.caddy`, substituted by hand here and set in the shared ingress's own environment for step 4.
3. Start with `docker compose up -d --build`. The app and database publish no host ports; only the app joins `mcp-public`.
4. Copy `caddy/routetimer.caddy` into the shared ingress drop-in directory, validate Caddy, then reload it.
