#!/usr/bin/env bash
# Anjal first-time install on Ubuntu 24.04. Idempotent: safe to re-run
# after unpacking a new release tarball. Does NOT start the services -
# DEPLOY.md walks through configuration and first start.
#
#   sudo bash ./install.sh /path/to/anjal-release   # directory with server/, webmail/, bin/
#
# Run it through bash: a release built on Windows arrives without executable
# bits (measured on the production VM, 26 Sep 2026), this script included.
set -euo pipefail

RELEASE=${1:?usage: install.sh <release-dir>}
[ -d "$RELEASE/server" ] && [ -d "$RELEASE/webmail" ] && [ -d "$RELEASE/viewer" ] || { echo "release dir must contain server/, webmail/ and viewer/" >&2; exit 2; }

log() { echo "[install] $*"; }

# ---- user and directories ----
if ! id anjal >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/anjal --shell /usr/sbin/nologin --user-group anjal
  log "created user anjal"
fi
# rc.29 (DES-22 §4.1, D-358): the viewer runs as its own user; the webmail's user reaches its socket through
# the viewer's group, and nothing else of it.
if ! id anjal-viewer >/dev/null 2>&1; then
  useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin --user-group anjal-viewer
  log "created user anjal-viewer"
fi
usermod -aG anjal-viewer anjal
install -d -o root  -g root  -m 755 /opt/anjal /opt/anjal/bin
install -d -o root  -g anjal -m 750 /etc/anjal
install -d -o anjal -g anjal -m 750 /var/lib/anjal
install -d -o anjal -g anjal -m 700 /var/lib/anjal/acme /var/lib/anjal/backup /var/lib/anjal/webmail-keys
install -d -o anjal -g anjal -m 700 /var/mail/anjal
install -d -o anjal -g anjal -m 750 /var/lib/anjal/evidence

# ---- binaries (self-contained publish output) ----
for app in server webmail viewer; do
  rm -rf "/opt/anjal/$app.new"
  cp -a "$RELEASE/$app" "/opt/anjal/$app.new"
  chown -R root:root "/opt/anjal/$app.new"
  find "/opt/anjal/$app.new" -type d -exec chmod 755 {} +
  find "/opt/anjal/$app.new" -type f -exec chmod 644 {} +
  chmod 755 "/opt/anjal/$app.new/Anjal.Server" 2>/dev/null || true
  chmod 755 "/opt/anjal/$app.new/Anjal.Webmail" 2>/dev/null || true
  chmod 755 "/opt/anjal/$app.new/Anjal.Viewer" 2>/dev/null || true
  rm -rf "/opt/anjal/$app.old"
  [ -d "/opt/anjal/$app" ] && mv "/opt/anjal/$app" "/opt/anjal/$app.old"
  mv "/opt/anjal/$app.new" "/opt/anjal/$app"
  log "installed /opt/anjal/$app"
done
install -o root -g root -m 755 "$RELEASE/bin/backup.sh"  /opt/anjal/bin/backup.sh
install -o root -g root -m 755 "$RELEASE/bin/restore.sh" /opt/anjal/bin/restore.sh
# Outbound firewall (rc.8, INC-01): installed, not applied - see DEPLOY.md 13f.
install -o root -g root -m 755 "$RELEASE/bin/anjal-firewall.sh"  /opt/anjal/bin/anjal-firewall.sh
install -o root -g root -m 755 "$RELEASE/bin/anjal-portcheck.sh" /opt/anjal/bin/anjal-portcheck.sh
# rc.29: asks the viewer to read a small PDF (DEPLOY.md, rc.29 checks).
install -o root -g root -m 755 "$RELEASE/bin/viewer_check.py" /opt/anjal/bin/viewer_check.py

# ---- config templates (never overwrite a real config) ----
for f in server.env webmail.env rclone.conf; do
  if [ ! -f "/etc/anjal/$f" ]; then
    install -o root -g anjal -m 640 "$RELEASE/bin/$f.example" "/etc/anjal/$f"
    log "wrote template /etc/anjal/$f - EDIT IT before starting"
  fi
done

# ---- systemd ----
for u in anjal-server.service anjal-webmail.service anjal-viewer.service anjal-backup.service anjal-backup.timer; do
  install -o root -g root -m 644 "$RELEASE/bin/$u" "/etc/systemd/system/$u"
done
systemctl daemon-reload
systemctl enable anjal-server.service anjal-webmail.service anjal-viewer.service >/dev/null
# The backup timer is left off until backups are configured and one run has
# passed (DEPLOY.md section 11). Enabled here, it came alive at the next boot
# and ran nightly against the unconfigured template (DEF-062).
log "units installed; server, webmail and viewer enabled (not started); backup timer left off until DEPLOY.md section 11"

# ---- allow the anjal user to run pg_dump/psql via peer auth if local ----
if command -v psql >/dev/null 2>&1; then
  log "PostgreSQL client present"
else
  log "WARNING: psql/pg_dump not found - install postgresql-client before enabling backups"
fi
if ! command -v rclone >/dev/null 2>&1; then
  log "WARNING: rclone not found - install it before enabling backups (see DEPLOY.md)"
fi

log "done. Next: edit /etc/anjal/*.env, then follow DEPLOY.md section 6."
