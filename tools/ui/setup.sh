#!/usr/bin/env bash
# Anjal screen checks (rc.15, items 47 and UX-09): a throwaway copy to check.
#
# Makes a new, empty database with the shipped schema, starts the mail server and the webmail
# from an existing build, and fills one mailbox with the sample mail (tools/seed_sample_mail.py),
# so every screen has something to show. One check presses every button, Delete included:
# never point this at a real server or a real database.
#
# Needs: PostgreSQL on localhost with the postgres password in PGPASSWORD, psql, python3, and a
# build of the solution (dotnet build -c Release, or set CONFIG=Debug).
#
#   tools/ui/setup.sh            start (database anjal_ui, webmail on 5080)
#   tools/ui/setup.sh stop       stop the server and webmail it started
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CONFIG="${CONFIG:-Release}"
DB="${ANJAL_UI_DB:-anjal_ui}"
WORK="${ANJAL_UI_WORK:-${RUNNER_TEMP:-/tmp}/anjal-ui}"
WEB_PORT="${ANJAL_UI_WEB_PORT:-5080}"
API_PORT="${ANJAL_UI_API_PORT:-5081}"
SMTP_PORT="${ANJAL_UI_SMTP_PORT:-2525}"
ADDRESS="${ANJAL_UI_ADDRESS:-arun@qa.test}"
PASSWORD="${ANJAL_UI_PASSWORD:-Lotus-Garden-42}"
TOKEN="$(openssl rand -hex 24)"

stop() {
  for f in "$WORK"/server.pid "$WORK"/webmail.pid; do
    if [ -f "$f" ]; then
      kill "$(cat "$f")" 2>/dev/null || true
      rm -f "$f"
    fi
  done
}

if [ "${1:-}" = "stop" ]; then
  stop
  echo "stopped"
  exit 0
fi

: "${PGPASSWORD:?Set PGPASSWORD to the postgres password}"
export PGPASSWORD
stop
rm -rf "$WORK"
mkdir -p "$WORK/maildir" "$WORK/evidence" "$WORK/keys"

# 1. A new database with the shipped schema.
psql -h localhost -U postgres -q -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS $DB;" -c "CREATE DATABASE $DB;"
psql -h localhost -U postgres -d "$DB" -q -v ON_ERROR_STOP=1 -f "$ROOT/tools/sql/schema.sql"

COMMON=(
  "ANJAL_POSTGRES=Host=localhost;Database=$DB;Username=postgres;Password=$PGPASSWORD"
  "ANJAL_MAILDIR_ROOT=$WORK/maildir" "ANJAL_EVIDENCE_ROOT=$WORK/evidence" "ANJAL_HOSTNAME=qa.test"
  "ANJAL_PWNED_MODE=off" "ANJAL_GEO_MODE=off" "ANJAL_DNS_CHECK=off"
)

wait_for() {
  for _ in $(seq 1 60); do
    if curl -s -o /dev/null "$1"; then
      return 0
    fi
    sleep 1
  done
  echo "::error::$2 did not start; its log follows"
  cat "$3"
  exit 1
}

# 2. The mail server (receives the sample mail; its API makes the organisation and mailbox).
env "${COMMON[@]}" ANJAL_GREYLIST=false ANJAL_SPAM_DNS=false ANJAL_PORT="$SMTP_PORT" \
  ANJAL_API_PORT="$API_PORT" ANJAL_API_TOKEN="$TOKEN" \
  setsid dotnet "$ROOT/src/Anjal.Server/bin/$CONFIG/net10.0/Anjal.Server.dll" > "$WORK/server.log" 2>&1 < /dev/null &
echo $! > "$WORK/server.pid"
wait_for "http://127.0.0.1:$API_PORT/" "The mail server" "$WORK/server.log"

# 3. The sample mail, spread over ten days.
python3 "$ROOT/tools/seed_sample_mail.py" --token "$TOKEN" --password "$PASSWORD" --address "$ADDRESS" \
  --smtp "127.0.0.1:$SMTP_PORT" --api "http://127.0.0.1:$API_PORT" --backdate-db "$DB"

# The person checked is the organisation's administrator (its postmaster) and an operator.
psql -h localhost -U postgres -d "$DB" -q -v ON_ERROR_STOP=1 \
  -c "UPDATE tenants SET postmaster_mailbox = '$ADDRESS' WHERE slug = 'qa';"
# Owner, 9 Oct: some sample mail arrived encrypted (no lock), so the lists show mail with and
# without the lock, and the "lineup" check sees that every subject starts at the same place.
psql -h localhost -U postgres -d "$DB" -q -v ON_ERROR_STOP=1 -c "UPDATE messages SET transport_encrypted = true
  WHERE id IN (SELECT id FROM messages ORDER BY received_at DESC OFFSET 1 LIMIT 3);"
# ...and their organisation gives them Send one each (owner, 8 Oct 2026: only for people it chooses),
# so the screens that use it are checked too.
psql -h localhost -U postgres -d "$DB" -q -v ON_ERROR_STOP=1 -c "INSERT INTO tenant_documents (tenant_id, kind, body)
  SELECT t.id, 'merge-access', json_build_object('People', json_build_array(m.id))::text
  FROM tenants t JOIN mailboxes m ON m.tenant_id = t.id
  WHERE t.slug = 'qa' AND m.local_part || '@' || m.domain = '$ADDRESS'
  ON CONFLICT (tenant_id, kind) DO UPDATE SET body = EXCLUDED.body;"

# 4. The webmail.
env "${COMMON[@]}" ANJAL_WEBMAIL_PORT="$WEB_PORT" ANJAL_WEBMAIL_KEYS_DIR="$WORK/keys" \
  ANJAL_OPERATORS="$ADDRESS" ANJAL_BACKUP_STATUS="$WORK/backup-status" \
  setsid dotnet "$ROOT/src/Anjal.Webmail/bin/$CONFIG/net10.0/Anjal.Webmail.dll" > "$WORK/webmail.log" 2>&1 < /dev/null &
echo $! > "$WORK/webmail.pid"
wait_for "http://127.0.0.1:$WEB_PORT/sign-in" "The webmail" "$WORK/webmail.log"
echo "ready: http://127.0.0.1:$WEB_PORT  ($ADDRESS)"
