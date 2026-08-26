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

# docker compose interpolates every service's environment before running any command, even an
# exec against a single service -- so the local model's required GARMIN_TOKEN_ENCRYPTION_KEY must
# resolve here too, even though this script never touches the routetimer service itself. The
# homelab model has no such file next to its compose file, so this is a no-op there.
COMPOSE_ARGS=(-f "$COMPOSE_FILE")
ENV_FILE="$(dirname "$COMPOSE_FILE")/.env.local"
if [ -f "$ENV_FILE" ]; then
	COMPOSE_ARGS+=(--env-file "$ENV_FILE")
fi

mkdir -p "$OUT_DIR"
docker compose "${COMPOSE_ARGS[@]}" exec -T routetimer-db \
	pg_dump -Fc -U "$DB_USER" "$DB_NAME" > "$OUT_FILE"

echo "Backup written to $OUT_FILE"
