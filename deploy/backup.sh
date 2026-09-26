#!/usr/bin/env bash
# Anjal backup: PostgreSQL dump first, then the Maildir tree and the ACME
# store, synced to Backblaze B2 with rclone's crypt backend (client-side
# encryption). Order matters: the database index is captured before the
# files, so a restored index never references a file that was not backed
# up. Maildir files are written whole then renamed, so a sync while mail
# is arriving is safe.
#
# Requires (see DEPLOY.md): rclone with a remote named "b2crypt" defined in
# /etc/anjal/rclone.conf, pg_dump, and ANJAL_POSTGRES in the environment.
#
# Layout in the bucket:
#   db/anjal-YYYYMMDD-HHMMSS.sql.gz    (last 30 kept)
#   mail/<tenant>/<mailbox>/...        (mirror of /var/mail/anjal)
#   acme/...                           (account key, cert, key, meta)
#   deleted/YYYYMMDD/...               (files removed from the mirror, 30 days)
#
# v1.0.0-rc.6: refuses to start until backups are configured, never leaves
# the unencrypted dump behind however it ends (DEF-062), and verifies every
# upload with real checksums through the encryption - `rclone check` could
# only compare sizes through crypt, and a failed check did not fail the run
# (DEF-061). Any verification failure now fails the run.
set -euo pipefail

RCLONE_CONFIG=${RCLONE_CONFIG:-/etc/anjal/rclone.conf}
REMOTE=${ANJAL_BACKUP_REMOTE:-b2crypt:}
MAILDIR_ROOT=${ANJAL_MAILDIR_ROOT:-/var/mail/anjal}
ACME_DIR=${ANJAL_ACME_DIR:-/var/lib/anjal/acme}
SCRATCH=${ANJAL_BACKUP_SCRATCH:-/var/lib/anjal/backup}
KEEP_DUMPS=${ANJAL_BACKUP_KEEP_DUMPS:-30}
STAMP=$(date -u +%Y%m%d-%H%M%S)

export RCLONE_CONFIG
mkdir -p "$SCRATCH"
chmod 700 "$SCRATCH"

log() { echo "[backup $(date -u +%H:%M:%S)] $*"; }

# ---- 0. Configured? Nothing is dumped until it is (DEF-062) ----
# Command output is read whole, then searched: with pipefail, "rclone ... |
# grep -q" fails whenever grep stops reading early and rclone gets SIGPIPE.
REMOTE_NAME=${REMOTE%%:*}
REMOTES=$(rclone listremotes 2>/dev/null || true)
if [ ! -r "$RCLONE_CONFIG" ] || grep -q 'CHANGE-ME' "$RCLONE_CONFIG" \
   || ! grep -qxF "$REMOTE_NAME:" <<< "$REMOTES"; then
  log "backups are not configured: $RCLONE_CONFIG is missing, unreadable, still holds CHANGE-ME placeholders, or has no remote \"$REMOTE_NAME\" (DEPLOY.md section 11). Nothing was dumped."
  exit 1
fi

# Through a crypt remote only cryptcheck compares real checksums; plain
# check falls back to sizes and misses a damaged or altered file (DEF-061).
REMOTE_CONF=$(rclone config show "$REMOTE_NAME" 2>/dev/null || true)
if grep -qE '^type *= *crypt' <<< "$REMOTE_CONF"; then
  verify() { rclone cryptcheck "$@" --one-way --quiet; }
else
  verify() { rclone check "$@" --one-way --quiet; }
fi

# ---- 1. Database ----
# ANJAL_POSTGRES is an Npgsql connection string; pg_dump wants libpq form.
# Convert "Host=..;Port=..;Database=..;Username=..;Password=.." to env vars.
IFS=';' read -ra PARTS <<< "${ANJAL_POSTGRES:?ANJAL_POSTGRES is not set}"
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
DUMP="$SCRATCH/anjal-$STAMP.sql.gz"
# The dump is unencrypted: remove it however this script ends (DEF-062).
trap 'rm -f "$DUMP"' EXIT
log "pg_dump ${PGDATABASE:-anjal} -> $DUMP"
pg_dump --no-owner --no-privileges | gzip -6 > "$DUMP"
log "dump size: $(du -h "$DUMP" | cut -f1)"

# ---- 2. Upload dump, prune old dumps ----
rclone copy "$DUMP" "${REMOTE}db/" --stats=0 --quiet
verify "$SCRATCH" "${REMOTE}db/" --include "$(basename "$DUMP")"
log "dump uploaded and verified"
rm -f "$DUMP"
# Keep the newest $KEEP_DUMPS dumps.
rclone lsf "${REMOTE}db/" --files-only | sort | head -n -"$KEEP_DUMPS" | while read -r old; do
  [ -n "$old" ] && rclone deletefile "${REMOTE}db/$old" --quiet && log "pruned db/$old"
done || true

# ---- 3. Maildir mirror (versioned deletes) ----
log "sync $MAILDIR_ROOT -> ${REMOTE}mail/"
rclone sync "$MAILDIR_ROOT" "${REMOTE}mail/" \
  --backup-dir "${REMOTE}deleted/$(date -u +%Y%m%d)/" \
  --exclude 'tmp/**' \
  --transfers 8 --checkers 16 --fast-list --stats=0 --quiet

# ---- 4. ACME store (account key, certificate, key) ----
if [ -d "$ACME_DIR" ]; then
  log "sync $ACME_DIR -> ${REMOTE}acme/"
  rclone sync "$ACME_DIR" "${REMOTE}acme/" --exclude 'renew.request' --exclude '*.tmp' --stats=0 --quiet
  verify "$ACME_DIR" "${REMOTE}acme/" --exclude 'renew.request' --exclude '*.tmp'
fi

# ---- 5. Prune old deleted-file archives (30 days) ----
rclone delete "${REMOTE}deleted/" --min-age 30d --quiet 2>/dev/null || true
rclone rmdirs "${REMOTE}deleted/" --leave-root --quiet 2>/dev/null || true

# ---- 6. Verify ----
# A plain command, not "cmd && log": under set -e a failure inside an &&
# list does not stop the script, so a detected difference used to end in
# "done" and success.
log "verify mail mirror"
verify "$MAILDIR_ROOT" "${REMOTE}mail/" --exclude 'tmp/**' --fast-list
log "verify ok"
log "done"
