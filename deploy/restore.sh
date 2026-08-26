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

# See the matching comment in backup.sh: docker compose interpolates every service's environment
# before running any command, so the local model's required GARMIN_TOKEN_ENCRYPTION_KEY must
# resolve even for this routetimer-db-only exec.
COMPOSE_ARGS=(-f "$COMPOSE_FILE")
ENV_FILE="$(dirname "$COMPOSE_FILE")/.env.local"
if [ -f "$ENV_FILE" ]; then
	COMPOSE_ARGS+=(--env-file "$ENV_FILE")
fi

docker compose "${COMPOSE_ARGS[@]}" exec -T routetimer-db \
	pg_restore --clean --if-exists -U "$DB_USER" -d "$DB_NAME" < "$DUMP_FILE"

echo "Restored from $DUMP_FILE"
