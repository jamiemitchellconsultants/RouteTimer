# RouteTimer deployment

1. Set `ROUTETIMER_DB_PASSWORD`, `KEYCLOAK_AUTHORITY` (for example `https://auth.example.com/realms/routetimer`), and `ROUTETIMER_HOSTNAME` in the deployment environment. The public authority and hostname are build arguments because the WASM OIDC configuration is a static asset.
2. Import `keycloak/routetimer-realm.json` into the existing Keycloak instance, replacing `ROUTETIMER_HOSTNAME` first. Assign the realm `rider` role to the rider account.
3. Start with `docker compose up -d --build`. The app and database publish no host ports; only the app joins `mcp-public`.
4. Copy `caddy/routetimer.caddy` into the shared ingress drop-in directory, validate Caddy, then reload it.
