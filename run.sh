#!/usr/bin/env bash
set -euo pipefail

PORT="${ROUTETIMER_PORT:-49215}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$ROOT/deploy/docker-compose.local.yml"
ENV_FILE="$ROOT/deploy/.env.local"

if ! command -v docker >/dev/null 2>&1; then
	echo "Docker is not installed. See RUNBOOK.md, Step 1." >&2
	exit 1
fi

if ! docker info >/dev/null 2>&1; then
	echo "Docker is installed but not running. Start Docker Desktop, then run this again." >&2
	exit 1
fi

if [ ! -f "$ENV_FILE" ]; then
	if ! command -v openssl >/dev/null 2>&1; then
		echo "openssl is required to generate the Garmin token encryption key and was not found." >&2
		exit 1
	fi
	echo "First run: generating a Garmin token encryption key..."
	# This key encrypts your Garmin account tokens at rest -- generated once, kept in this
	# git-ignored file, and reused on every future start. Losing it makes any stored Garmin
	# connection unreadable; RouteTimer's own training data and predictions are unaffected.
	echo "GARMIN_TOKEN_ENCRYPTION_KEY=$(openssl rand -base64 32)" > "$ENV_FILE"
	chmod 600 "$ENV_FILE"
fi

if ! grep -q '^GOOGLE_MAPS_KEY_ENCRYPTION_KEY=' "$ENV_FILE" 2>/dev/null; then
	if ! command -v openssl >/dev/null 2>&1; then
		echo "openssl is required to generate the Google Maps key encryption key and was not found." >&2
		exit 1
	fi
	echo "Generating a Google Maps key encryption key..."
	# Encrypts a rider's saved Google Maps API key at rest, the same way the Garmin key above
	# protects Garmin tokens. Unlike that key, this one is optional at the application level --
	# but run.sh always generates it so a rider who saves a Google Maps key on day one doesn't
	# lose it to a key that was never provisioned.
	echo "GOOGLE_MAPS_KEY_ENCRYPTION_KEY=$(openssl rand -base64 32)" >> "$ENV_FILE"
fi

echo "Starting RouteTimer on port $PORT..."
if ! ROUTETIMER_PORT="$PORT" docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" up -d --pull always --wait; then
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
