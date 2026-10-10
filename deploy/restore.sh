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
EVIDENCE_ROOT=${ANJAL_EVIDENCE_ROOT:-/var/lib/anjal/evidence}
ACME_DIR=${ANJAL_ACME_DIR:-/var/lib/anjal/acme}
SEAL_KEY=${ANJAL_DKIM_SEAL_KEY:-/var/lib/anjal/dkim-seal.pem}
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
# The DKIM seal key (rc.15, DES-11 S6): it opens every DKIM key in the database.
if rclone lsf "${REMOTE}keys/" 2>/dev/null | grep -qx 'dkim-seal.pem'; then
  log "restore ${REMOTE}keys/dkim-seal.pem -> $SEAL_KEY"
  mkdir -p "$(dirname "$SEAL_KEY")"
  rclone copyto "${REMOTE}keys/dkim-seal.pem" "$SEAL_KEY" --stats=0 --quiet
  chown anjal:anjal "$SEAL_KEY"
  chmod 600 "$SEAL_KEY"
fi
find "$MAILDIR_ROOT" -type d -exec chmod 700 {} +
find "$MAILDIR_ROOT" -type f -exec chmod 600 {} +
# Evidence (v1.0.0-rc.8): the originals and the manifest chain, read-only again.
log "restore ${REMOTE}evidence/ -> $EVIDENCE_ROOT"
mkdir -p "$EVIDENCE_ROOT"
rclone copy "${REMOTE}evidence/" "$EVIDENCE_ROOT" --transfers 8 --checkers 16 --fast-list --stats=0 --quiet
chown -R anjal:anjal "$EVIDENCE_ROOT"
find "$EVIDENCE_ROOT" -type d -exec chmod 750 {} +
find "$EVIDENCE_ROOT" -type f -name '*.eml' -exec chmod 440 {} +
find "$EVIDENCE_ROOT/manifests" -type f -exec chmod 440 {} + 2>/dev/null || true
# Greylist memory back into place, and the settings snapshot beside
# /etc/anjal for comparison only - never over it: secrets come from paper
# (v1.0.0-rc.7).
STATE_DIR=${ANJAL_STATE_DIR:-/var/lib/anjal}
if [ -n "$(rclone lsf "${REMOTE}settings/" --files-only 2>/dev/null)" ]; then
  mkdir -p "$STATE_DIR/restored-settings"
  rclone copy "${REMOTE}settings/" "$STATE_DIR/restored-settings/" --exclude greylist.tsv --stats=0 --quiet
  if rclone lsf "${REMOTE}settings/" --files-only 2>/dev/null | grep -qx 'greylist.tsv'; then
    rclone copyto "${REMOTE}settings/greylist.tsv" "$STATE_DIR/greylist.tsv" --stats=0 --quiet
    chown anjal:anjal "$STATE_DIR/greylist.tsv"; chmod 600 "$STATE_DIR/greylist.tsv"
  fi
  chown -R anjal:anjal "$STATE_DIR/restored-settings"
  chmod 700 "$STATE_DIR/restored-settings"; find "$STATE_DIR/restored-settings" -type f -exec chmod 600 {} +
  log "settings snapshot (secrets removed) in $STATE_DIR/restored-settings - compare with /etc/anjal; greylist memory restored"
fi
log "files restored"

if [ "$FILES_ONLY" = "1" ]; then
  log "done (files only)"
  exit 0
fi

# ---- 2. Database ----
# Read ANJAL_POSTGRES from the server env file (this script runs as root) as
# text, the way systemd reads it (EnvironmentFile). The file is not a shell
# script: run through bash, the ';' between the connection's parts separates
# commands, ANJAL_POSTGRES became "Host=127.0.0.1" and psql connected as root
# (DEF-083, found by the restore drill of 1 Oct 2026). One pair of
# surrounding quotes is removed, as systemd does.
ENV_FILE=${ANJAL_ENV_FILE:-/etc/anjal/server.env}
ANJAL_POSTGRES=$(sed -n 's/^ANJAL_POSTGRES=//p' "$ENV_FILE" | tail -n 1 | tr -d '\r')
case "$ANJAL_POSTGRES" in
  \"*\") ANJAL_POSTGRES=${ANJAL_POSTGRES#\"}; ANJAL_POSTGRES=${ANJAL_POSTGRES%\"} ;;
esac
IFS=';' read -ra PARTS <<< "${ANJAL_POSTGRES:?ANJAL_POSTGRES is not set in $ENV_FILE}"
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

# Prove the connection before deciding anything: a failed connection used to
# read as "0 tenants" below and only failed later, at DROP SCHEMA (DEF-083).
if ! psql -tAc 'SELECT 1' >/dev/null; then
  echo "Cannot connect to the database as ${PGUSER:-?} on ${PGHOST:-?}:${PGPORT:-5432} - check ANJAL_POSTGRES in $ENV_FILE. Nothing in the database was changed." >&2
  exit 1
fi

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
log "done - start the services: systemctl start anjal-server anjal-webmail (a restore drill writes its isolation into the database first - DEPLOY.md 12.4)"
