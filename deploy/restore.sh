#!/usr/bin/env bash
# Anjal restore: bring back the Maildir tree, the ACME store and (optionally)
# the database from Backblaze B2. Run as root on a freshly installed VM
# after install.sh, BEFORE starting the services.
#
#   restore.sh              # files + latest database dump
#   restore.sh --files-only # Maildir + ACME only (keep the current database)
#   restore.sh --dump NAME  # restore a specific dump listed by `rclone lsf b2crypt:db/`
#
# The database restore drops and recreates the schema objects from the
# dump; it refuses to run if the target database already has tenants
# unless --force is given.
set -euo pipefail

RCLONE_CONFIG=${RCLONE_CONFIG:-/etc/anjal/rclone.conf}
REMOTE=${ANJAL_BACKUP_REMOTE:-b2crypt:}
MAILDIR_ROOT=${ANJAL_MAILDIR_ROOT:-/var/mail/anjal}
ACME_DIR=${ANJAL_ACME_DIR:-/var/lib/anjal/acme}
SCRATCH=${ANJAL_BACKUP_SCRATCH:-/var/lib/anjal/backup}
export RCLONE_CONFIG

FILES_ONLY=0; DUMP=""; FORCE=0
while [ $# -gt 0 ]; do
  case "$1" in
    --files-only) FILES_ONLY=1 ;;
    --dump) DUMP="$2"; shift ;;
    --force) FORCE=1 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done

log() { echo "[restore $(date -u +%H:%M:%S)] $*"; }

if systemctl is-active --quiet anjal-server || systemctl is-active --quiet anjal-webmail; then
  echo "Stop anjal-server and anjal-webmail before restoring." >&2
  exit 1
fi

# ---- 1. Files ----
log "restore ${REMOTE}mail/ -> $MAILDIR_ROOT"
mkdir -p "$MAILDIR_ROOT"
rclone sync "${REMOTE}mail/" "$MAILDIR_ROOT" --transfers 8 --checkers 16 --fast-list --stats=0 --quiet
log "restore ${REMOTE}acme/ -> $ACME_DIR"
mkdir -p "$ACME_DIR"
rclone sync "${REMOTE}acme/" "$ACME_DIR" --stats=0 --quiet
chown -R anjal:anjal "$MAILDIR_ROOT" "$ACME_DIR"
chmod 700 "$ACME_DIR"
find "$ACME_DIR" -name '*.key.pem' -exec chmod 600 {} +
find "$MAILDIR_ROOT" -type d -exec chmod 700 {} +
find "$MAILDIR_ROOT" -type f -exec chmod 600 {} +
log "files restored"

if [ "$FILES_ONLY" = "1" ]; then
  log "done (files only)"
  exit 0
fi

# ---- 2. Database ----
# Read ANJAL_POSTGRES from the server env file (this script runs as root).
# shellcheck disable=SC1091
set -a; . /etc/anjal/server.env; set +a
IFS=';' read -ra PARTS <<< "${ANJAL_POSTGRES:?ANJAL_POSTGRES is not set in /etc/anjal/server.env}"
for kv in "${PARTS[@]}"; do
  key=${kv%%=*}; val=${kv#*=}
  case "${key,,}" in
    host|server)  export PGHOST="$val" ;;
    port)         export PGPORT="$val" ;;
    database)     export PGDATABASE="$val" ;;
    username|user|user\ id) export PGUSER="$val" ;;
    password)     export PGPASSWORD="$val" ;;
  esac
done

if [ -z "$DUMP" ]; then
  DUMP=$(rclone lsf "${REMOTE}db/" --files-only | sort | tail -n 1)
fi
[ -n "$DUMP" ] || { echo "no database dump found in ${REMOTE}db/" >&2; exit 1; }

EXISTING=$(psql -tAc "SELECT count(*) FROM tenants" 2>/dev/null || echo 0)
if [ "$EXISTING" != "0" ] && [ "$FORCE" != "1" ]; then
  echo "Database already has $EXISTING tenant(s). Re-run with --force to overwrite." >&2
  exit 1
fi

mkdir -p "$SCRATCH"; chmod 700 "$SCRATCH"
log "fetch db/$DUMP"
rclone copy "${REMOTE}db/$DUMP" "$SCRATCH/" --stats=0 --quiet
log "restore database ${PGDATABASE:-anjal} from $DUMP"
psql -v ON_ERROR_STOP=1 -q -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"
gunzip -c "$SCRATCH/$DUMP" | psql -v ON_ERROR_STOP=1 -q
rm -f "$SCRATCH/$DUMP"
log "database restored; tenants: $(psql -tAc 'SELECT count(*) FROM tenants')"
log "done - start the services: systemctl start anjal-server anjal-webmail"
