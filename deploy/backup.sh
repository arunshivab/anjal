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
#   evidence/YYYY/MM/DD/<id>.eml       (rc.8: originals, append-only - never mirrors a deletion)
#   evidence/manifests/YYYY-MM-DD.txt  (rc.8: the daily manifest chain)
#   deleted/YYYYMMDD/...               (files removed from the mirror, 30 days)
#
# v1.0.0-rc.6: refuses to start until backups are configured, never leaves
# the unencrypted dump behind however it ends (DEF-062), and verifies every
# upload with real checksums through the encryption - `rclone check` could
# only compare sizes through crypt, and a failed check did not fail the run
# (DEF-061). Any verification failure now fails the run.
set -euo pipefail

STATUS_FILE=${ANJAL_BACKUP_STATUS:-/var/lib/anjal/backup-status}

# ---- D-88 (owner, 10 Oct 2026): try for about 30 minutes, then say so ----
# A run that fails is tried again after 2, 4, 8 and 16 minutes - 30 minutes in
# all - so a short network or Backblaze outage does not cost a night's backup.
# When the last try fails too, a "failed" line is added to the status record,
# beside the "ok" line of the last good backup: the Anjal console's "Last good
# backup" turns red at once and the operators are mailed within 15 minutes,
# instead of when the last good backup is 48 hours old. "Not configured"
# (exit 3) is not tried again: waiting does not configure it.
if [ -z "${ANJAL_BACKUP_TRY:-}" ]; then
  WAITS=${ANJAL_BACKUP_RETRY_WAITS-120 240 480 960}
  try=1
  code=0
  for wait in $WAITS last; do
    if ANJAL_BACKUP_TRY=$try "$BASH" "$0" "$@"; then
      exit 0
    else
      code=$?
    fi
    if [ "$code" -eq 3 ] || [ "$wait" = last ]; then
      break
    fi
    echo "[backup $(date -u +%H:%M:%S)] try $try failed (exit $code); trying again in $wait s"
    sleep "$wait"
    try=$((try + 1))
  done
  good=$(grep -m1 '^ok ' "$STATUS_FILE" 2>/dev/null || true)
  {
    if [ -n "$good" ]; then echo "$good"; fi
    printf 'failed %s after %s tries (exit %s)\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$try" "$code"
  } > "$STATUS_FILE.tmp" && chmod 644 "$STATUS_FILE.tmp" && mv "$STATUS_FILE.tmp" "$STATUS_FILE"
  echo "[backup $(date -u +%H:%M:%S)] FAILED after $try tries (exit $code); recorded in $STATUS_FILE for the Anjal console and the operators' alert"
  exit "$code"
fi

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
  exit 3
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
SNAP="$SCRATCH/settings"
trap 'rm -f "$DUMP"; rm -rf "$SNAP"' EXIT
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

# ---- 3b. Evidence (v1.0.0-rc.8, ANJAL-DES-01): append-only ----
# Copy, never sync: the backup keeps every original, even one lost or damaged
# on the server. A file leaves the backup only when the retention rule purges
# it: the evidence worker names each purged file in a purge list, and only
# those files are removed here. A list for a finished day is then marked done.
EVIDENCE_ROOT=${ANJAL_EVIDENCE_ROOT:-/var/lib/anjal/evidence}
if [ -d "$EVIDENCE_ROOT" ]; then
  log "copy $EVIDENCE_ROOT -> ${REMOTE}evidence/ (append-only)"
  rclone copy "$EVIDENCE_ROOT" "${REMOTE}evidence/" \
    --exclude 'purge-lists/**' --exclude '*.tmp' \
    --transfers 8 --checkers 16 --fast-list --stats=0 --quiet
  TODAY_UTC=$(date -u +%Y-%m-%d)
  for list in "$EVIDENCE_ROOT"/purge-lists/*.txt; do
    [ -e "$list" ] || continue
    log "apply purge list $(basename "$list") to ${REMOTE}evidence/"
    rclone delete "${REMOTE}evidence/" --files-from "$list" --quiet
    if [ "$(basename "$list" .txt)" \< "$TODAY_UTC" ]; then
      mv "$list" "$list.done"
    fi
  done
fi

# ---- 4. ACME store (account key, certificate, key) ----
if [ -d "$ACME_DIR" ]; then
  log "sync $ACME_DIR -> ${REMOTE}acme/"
  rclone sync "$ACME_DIR" "${REMOTE}acme/" --exclude 'renew.request' --exclude '*.tmp' --stats=0 --quiet
  verify "$ACME_DIR" "${REMOTE}acme/" --exclude 'renew.request' --exclude '*.tmp'
fi

# ---- 4b. DKIM seal key (rc.15, DES-11 S6) ----
# The mail server's seal key locks every DKIM key in the database; without it
# those keys cannot be opened, and every domain would need a new DKIM record.
# It goes to the encrypted backup like the ACME keys.
SEAL_KEY=${ANJAL_DKIM_SEAL_KEY:-/var/lib/anjal/dkim-seal.pem}
if [ -r "$SEAL_KEY" ]; then
  log "copy the DKIM seal key -> ${REMOTE}keys/"
  rclone copyto "$SEAL_KEY" "${REMOTE}keys/dkim-seal.pem" --stats=0 --quiet
  verify "$(dirname "$SEAL_KEY")" "${REMOTE}keys/" --include "$(basename "$SEAL_KEY")"
fi
# ---- 5. Prune old deleted-file archives (30 days) ----
# Settings snapshot with every secret removed, and the greylist mirror
# (v1.0.0-rc.7). Settings also live in the database dump; this copy covers
# what the env files hold, so a rebuild can be compared against it. Secrets
# never leave the server: they are on the paper custody forms.
SETTINGS_DIR=${ANJAL_SETTINGS_DIR:-/etc/anjal}
GREYLIST_FILE=${ANJAL_GREYLIST_STATE:-/var/lib/anjal/greylist.tsv}
SECRET_KEYS='^[A-Za-z0-9_]*(POSTGRES|PASSWORD|TOKEN|SECRET|KEK)[A-Za-z0-9_]*='
rm -rf "$SNAP"; mkdir -p "$SNAP"; chmod 700 "$SNAP"
for f in "$SETTINGS_DIR/server.env" "$SETTINGS_DIR/webmail.env"; do
  [ -r "$f" ] || continue
  sed -E "s/(${SECRET_KEYS#^}).*/\1<secret removed - see the custody forms>/" "$f" > "$SNAP/$(basename "$f")"
done
if grep -hE "$SECRET_KEYS" "$SNAP"/*.env 2>/dev/null | grep -vq '<secret removed - see the custody forms>$'; then
  log "FAILED: a secret survived removal from the settings snapshot; nothing was uploaded"
  exit 1
fi
if [ -r "$GREYLIST_FILE" ] && [ "$GREYLIST_FILE" != "none" ]; then
  cp "$GREYLIST_FILE" "$SNAP/greylist.tsv"
fi
if [ -n "$(ls -A "$SNAP")" ]; then
  log "sync settings snapshot (secrets removed) and greylist -> ${REMOTE}settings/"
  rclone sync "$SNAP" "${REMOTE}settings/" --stats=0 --quiet
  verify "$SNAP" "${REMOTE}settings/"
fi
rm -rf "$SNAP"

rclone delete "${REMOTE}deleted/" --min-age 30d --quiet 2>/dev/null || true
rclone rmdirs "${REMOTE}deleted/" --leave-root --quiet 2>/dev/null || true

# ---- 6. Verify ----
# A plain command, not "cmd && log": under set -e a failure inside an &&
# list does not stop the script, so a detected difference used to end in
# "done" and success.
log "verify mail mirror"
verify "$MAILDIR_ROOT" "${REMOTE}mail/" --exclude 'tmp/**' --fast-list
if [ -d "$EVIDENCE_ROOT" ]; then
  log "verify evidence"
  verify "$EVIDENCE_ROOT" "${REMOTE}evidence/" --exclude 'purge-lists/**' --exclude '*.tmp' --fast-list
fi
log "verify ok"
log "done"

# v1.0.0-rc.14: a readable mark of the last good run, for the Anjal console's
# service health ("Last good backup"). Written only after everything verified;
# it replaces any "failed" line (D-88).
printf 'ok %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$STATUS_FILE.tmp" && chmod 644 "$STATUS_FILE.tmp" && mv "$STATUS_FILE.tmp" "$STATUS_FILE"
