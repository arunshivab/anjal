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
log "pg_dump ${PGDATABASE:-anjal} -> $DUMP"
pg_dump --no-owner --no-privileges | gzip -6 > "$DUMP"
log "dump size: $(du -h "$DUMP" | cut -f1)"

# ---- 2. Upload dump, prune old dumps ----
rclone copy "$DUMP" "${REMOTE}db/" --stats=0 --quiet
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
fi

# ---- 5. Prune old deleted-file archives (30 days) ----
rclone delete "${REMOTE}deleted/" --min-age 30d --quiet 2>/dev/null || true
rclone rmdirs "${REMOTE}deleted/" --leave-root --quiet 2>/dev/null || true

# ---- 6. Verify ----
log "verify mail mirror"
rclone check "$MAILDIR_ROOT" "${REMOTE}mail/" --exclude 'tmp/**' --one-way --fast-list --quiet && log "verify ok"
log "done"
