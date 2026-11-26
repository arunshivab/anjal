# Anjal: the first server from rc.27 straight to rc.30 (one install)

For **mail.anjal.co.in** (E2E Networks, 151.185.43.171, Ubuntu 24.04, systemd, 5.4 GiB, no swap), which runs
v1.0.0-rc.27 since 9 Nov 2026 and never had rc.28, rc.29 or rc.30. The release installed is **v1.0.0-rc.30.1**:
rc.30's code with its own version number (rc.30's `Directory.Build.props` still said rc.29, so `publish.ps1` refuses
to build it as rc.30), the Sangam test vector's offset pinned, and the Domains page accepting
`include:_spf.anjal.co.in` (Part B, step B2).

This one install brings everything rc.28, rc.29 and rc.30 change on the server:
- rc.28: IMAP for mail apps on 993, the secure link, chat, the mail import, and the re-seal of evidence kept before rc.25.
- rc.29: the `anjal-viewer` service and the PDF editor (Redact and page changes on files with hidden layers stay off).
- rc.30: DICOM, which stays off here.

It replaces DEPLOY.md 13d's "rc.27 to rc.29 in one install" for this server.

**How to use it.**
- Every block is pasted whole. A block ends wherever you must wait, look, or do something elsewhere.
- **PC** blocks go in PowerShell on your laptop. **Server** blocks go in the SSH window:
  `ssh -i C:\Users\aruns\.ssh\anjal_e2e arun@151.185.43.171`
- What a command should print is after its `#`.
- Every setting is read before it is written, and the block that writes it shows the old and the new value.
- **Stop and send the output to Claude** whenever something differs from what is written here.

**Not in this runbook:** WireGuard for SSH (DEPLOY.md 1e) is a separate step, never during an upgrade. Sangam runs on
its own VM (the owner, 25 Nov 2026), so nothing of Sangam's is installed here; Part B is only Anjal's side of
Sangam's mail.

**Time.** About an hour and a half. The only outage is the restart in Set 9: about 30 seconds of webmail. Mail
servers that try to deliver in that moment simply retry.

---

## Part A - the upgrade

### Set 0. The facts, read only (nothing changes)

**Server:**
```bash
cd ~
grep -o '"Anjal.Server/[^"]*"' /opt/anjal/server/Anjal.Server.deps.json | head -1       # "Anjal.Server/1.0.0-rc.27"
grep -o '"Anjal.Webmail/[^"]*"' /opt/anjal/webmail/Anjal.Webmail.deps.json | head -1    # "Anjal.Webmail/1.0.0-rc.27"
ls -d /opt/anjal/viewer /etc/systemd/system/anjal-viewer.service 2>&1 | grep -c 'No such file'   # 2
sudo ss -tlnp
sudo ss -ulnp
```

What `ss -tlnp` should show (TCP), and nothing else:

| Address | Port | Program |
|---|---|---|
| 0.0.0.0 (and/or [::]) | 22 | sshd |
| 0.0.0.0 | 25, 465, 587 | Anjal.Server |
| 0.0.0.0 | 80, 443 | Anjal.Webmail |
| 127.0.0.1 | 5432 | postgres |
| 127.0.0.1 | 8025 | Anjal.Server (the admin API) |
| 0.0.0.0 | 10050 | zabbix_agentd (closed by the Security Group) |

There must be **no** 993, no caddy, nginx, apache or httpd.

`ss -ulnp` (UDP) should show 443 held by Anjal.Webmail (HTTP/3), twice (two sockets). Nothing listens on 53: the E2E
image has no local DNS stub (seen on the first server, 25 Nov 2026); if a 127.0.0.53/54 line does show, that is
systemd-resolve and fine. 68 (DHCP) is normal if present. If **51820** shows, WireGuard (1e) has been set up: that is fine, just say so.

**Server:**
```bash
free -m
swapon --show; echo "swap lines above (none expected)"
df -h / /opt /var/lib/anjal /var/mail/anjal /var/lib/postgresql
for u in anjal-server anjal-webmail postgresql@18-main clamav-daemon; do printf '%-22s %s\n' $u "$(systemctl show -p MemoryCurrent --value $u)"; done
ps -C clamd -o rss=,etime=,args=
sudo grep -nE '^(ConcurrentDatabaseReload|StreamMaxLength|MaxScanSize|MaxFileSize)' /etc/clamav/clamd.conf; echo "clamd lines above"
sudo grep -nE '^Checks' /etc/clamav/freshclam.conf
sudo journalctl -k --since -30d --no-pager | grep -ciE 'out of memory|oom-kill'
systemctl list-units --type=service --state=running --no-pager --plain | grep -Ei 'anjal|caddy|nginx|apache|httpd|postgres|clam|zabbix|fail2ban'
systemctl list-unit-files --no-pager --plain | grep -E '^anjal'
ls -d /etc/systemd/system/anjal-*.d 2>/dev/null; echo "drop-in folders above (none expected)"
```

Expected:
- **Memory and swap:** `free -m` shows about 5,500 MB in all. Write down **`available`**: on 18 Nov it was about
  3,300 MB. `swapon` prints nothing (no swap).
- **Disk:** at least 2 GB free on `/` and on `/var/lib/postgresql`. The copies in Set 2 need about 1 GB.
- **Memory per service:** four numbers in bytes. Write them down, and clamd's `rss` (in KB) as well.
- **ClamAV settings:** `ConcurrentDatabaseReload` is usually not in the file, which means **yes**, the default.
  `StreamMaxLength` may be missing or different; Set 7 deals with it. `Checks` is the number of signature checks a
  day.
- **Out-of-memory kills:** 0 in the last 30 days.
- **Running services:** anjal-server, anjal-webmail, postgresql@18-main, clamav-daemon, clamav-freshclam,
  zabbix-agent and fail2ban. No caddy, nginx, apache or httpd.
- **Installed units:** anjal-backup.service, anjal-backup.timer, anjal-egress.service, anjal-portcheck.service,
  anjal-portcheck.timer, anjal-server.service and anjal-webmail.service. No anjal-viewer.
- **Drop-in folders:** none.

**Server** (the settings, as stored and as in the files):
```bash
sudo grep -HE '^ANJAL_(WEBMAIL_BIND|WEBMAIL_PORT|WEBMAIL_HTTPS_PORT|WEBMAIL_HTTP3|ACME_DOMAINS|ACME_HOST|ACME_STAGING|MTA_STS_[A-Z_]+|IMAP_TLS_PORT|VIEWER[A-Z_]*)=' /etc/anjal/webmail.env /etc/anjal/server.env
TOKEN=$(sudo grep '^ANJAL_API_TOKEN=' /etc/anjal/server.env | cut -d= -f2-)
curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/settings | jq -r '(.server + .webmail)[] | select(.key | test("WEBMAIL_(BIND|PORT|HTTPS_PORT|HTTP3)|ACME_(DOMAINS|HOST|STAGING)|MTA_STS|IMAP|VIEWER")) | "\(.scope) \(.key)=\(.value)"'
curl -s http://127.0.0.1:8025/healthz | jq -c .
curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/tenants | jq -r '.[] | "\(.slug) enabled=\(.enabled)"'
unset TOKEN
echo | openssl s_client -connect 127.0.0.1:443 -servername mail.anjal.co.in 2>/dev/null | openssl x509 -noout -subject -issuer -enddate -ext subjectAltName
cd /tmp && sudo -u postgres psql -d anjal -Atc "select count(*) from information_schema.tables where table_schema='public'"; cd ~
```

Expected:
- **Settings** (the database's values win over the files; DEPLOY.md 13e):
  - `ANJAL_WEBMAIL_BIND=0.0.0.0`, `ANJAL_WEBMAIL_PORT=80`, `ANJAL_WEBMAIL_HTTPS_PORT=443`;
  - `ANJAL_ACME_DOMAINS=mail.anjal.co.in`, or `mail.anjal.co.in,mta-sts.anjal.co.in` if 13i was done, **the same in
    both scopes**;
  - `ANJAL_ACME_HOST=true` for the webmail and **`false` for the server** (only the webmail answers Let's Encrypt
    on port 80); no `ANJAL_ACME_STAGING` that is not commented out;
  - the env files may list fewer `ANJAL_ACME_DOMAINS` than the database (13i stored the full list there); the
    database wins, and the start-up log says so;
  - `ANJAL_MTA_STS_MODE` testing or enforce, if 13i was done;
  - **no** `ANJAL_IMAP_TLS_PORT` and **no** `ANJAL_VIEWER...` anywhere.
- **Health:** `"status":"ok"`, with `store`, `maildir` and `tls` all ok.
- **Organisations:** `imagiqa enabled=true`, any others you made, and `sangamid` if Part B's organisation was
  already made. Note whether it is there.
- **Certificate:**
  - `subject=CN=mail.anjal.co.in`;
  - an issuer from Let's Encrypt (`O=Let's Encrypt`);
  - `notAfter` at least 30 days away (Anjal renews 30 days before);
  - the `Subject Alternative Name` lists the same names as `ANJAL_ACME_DOMAINS`.
- **Tables:** **43**.

**At E2E (the portal), read only:** the node's Security Group, inbound rules. Expected: TCP 22, 25, 80, 443, 465 and
587 and UDP 443 (and UDP 51820 if WireGuard is in). **No TCP 993 yet.**

If anything differs from this list, stop here and send the whole output.

---

### Decision M. Memory: M1 + M2, decided 25 Nov 2026, checked against Set 0's numbers

**The decision** (the coordinator, for Anjal, 25 Nov 2026):
- **M1:** the viewer's cap on this server is **1536M** while DICOM is off, by a drop-in file (Set 9);
- **M2:** ClamAV reloads its signatures **in place** (`ConcurrentDatabaseReload no`, Set 7);
- **no swap file;**
- an **8 GiB VM (M4)** is needed only before DICOM goes on here. That is the owner's call when the time comes: it
  costs money and a reboot.

Sangam gets its own VM (the owner, 25 Nov 2026), so nothing of Sangam's shares this server's memory.

**Why.** The viewer, `anjal-viewer`, reads every attachment shown in the mail window, in child processes, inside its
own memory cap. As shipped the cap is `MemoryMax=2048M`, raised in rc.30 for DICOM study grouping.
- With DICOM off, its worst case is two jobs at once, each stopped by the viewer itself past 768 MB, plus the
  supervisor and two children kept ready: about 1.7 GB if everything peaked together. The 1536M cap stops a job
  before that, inside the viewer's own share: the person sees "could not be shown", and nothing else on the machine is touched.
  This is the cap rc.29 was designed and tested at.
- `clamd` holds its signatures in memory (Set 0's `rss`). With `ConcurrentDatabaseReload yes`, the default, it loads
  a second copy before dropping the old one at every signature update, needing about twice that for a minute or so,
  several times a day.
- With no swap, if the whole machine runs out, the kernel kills the biggest process outside a cap. That could be
  PostgreSQL.

With the 18 Nov reading (3,300 MB available, clamd about 1,200 MB), the shipped 2048M plus a concurrent reload
leaves about −200 MB at the worst moment. M1 + M2 leaves about 1,500 MB.

What M2 costs: while clamd reloads (tens of seconds, a few times a day) it does not scan. Mail with files arriving in
that moment is answered "try later" (D-242), and senders retry within minutes. A file uploaded in the webmail in that
moment is refused once.

**The check, with Set 0's real numbers, before anything is installed.** Take Set 0's `free -m` `available` (MB) and
clamd's `rss` (KB). The worst-moment headroom with M1 + M2 is:

> headroom = available − 1,536 − 300

The 300 MB allows for rc.30's own growth and PostgreSQL's peaks. With M2, clamd's second copy no longer counts. To
see the reload's share, also work out the margin M2 is buying:

> without M2 = headroom − (clamd rss ÷ 1,024)

**Server** (works it out from the live numbers; read only):
```bash
avail=$(free -m | awk '/^Mem:/ {print $7}')
clam=$(( $(ps -C clamd -o rss= | awk '{s+=$1} END {print s+0}') / 1024 ))
echo "available ${avail} MB, clamd ${clam} MB"
echo "headroom with M1 + M2: $(( avail - 1536 - 300 )) MB    (needs at least 500)"
echo "without M2 it would be: $(( avail - 1536 - 300 - clam )) MB"
```
- **500 MB or more** with M1 + M2: go on to Set 1.
- **Under 500 MB:** **stop here, before Set 1,** and send these lines and Set 0's memory block to Claude. Nothing has
  been changed yet.

---

### Set 1. The release, built on the PC

v1.0.0-rc.30.1 must be merged and tagged first; Claude gives those steps with the code.

**PC:**
```powershell
cd C:\Users\aruns\Documents\Anjal\anjal-scaffold\anjal
git switch main
git pull
git describe --exact-match --tags HEAD     # v1.0.0-rc.30.1
git status --short                          # nothing
.\deploy\publish.ps1 -Version 1.0.0-rc.30.1
```
The last line says `Built ...\artifacts\anjal-1.0.0-rc.30.1.tar.gz`. This is the first build of the viewer for Linux
on your PC (trimmed and ReadyToRun). If it stops with an error, send the output.

**PC:**
```powershell
cd C:\Users\aruns\Documents\Anjal\anjal-scaffold\anjal
(Get-FileHash .\artifacts\anjal-1.0.0-rc.30.1.tar.gz -Algorithm SHA256).Hash.Substring(0,16)
scp -i C:\Users\aruns\.ssh\anjal_e2e .\artifacts\anjal-1.0.0-rc.30.1.tar.gz arun@151.185.43.171:~/
```
Write down the 16 characters.

---

### Set 2. Backups first: Backblaze, and copies on the server for a quick way back

**Server** (the nightly backup, now; it takes a few minutes):
```bash
T0=$(date '+%Y-%m-%d %H:%M:%S'); sudo systemctl start anjal-backup
sudo journalctl -u anjal-backup --since "$T0" --no-pager | grep -E "verify ok|done|FAIL|ERROR"
```
`verify ok` and `done` must both appear, with no `FAIL` or `ERROR`. If not, stop: DEPLOY.md section 11.

**Server** (rc.27's programs, settings, units and the database, copied on the server):
```bash
sudo install -d -m 700 /root/pre-rc30
sudo tar -czf /root/pre-rc30/opt-anjal.tgz -C / opt/anjal
sudo tar -czf /root/pre-rc30/etc.tgz -C / etc/anjal etc/clamav/clamd.conf usr/local/sbin/anjal-portcheck $(cd / && ls -d etc/systemd/system/anjal-*)
cd /tmp && sudo -u postgres pg_dump -Fc -d anjal -f /var/lib/postgresql/anjal-pre-rc30.dump; cd ~
sudo chmod 600 /var/lib/postgresql/anjal-pre-rc30.dump
sudo ls -l /root/pre-rc30 /var/lib/postgresql/anjal-pre-rc30.dump
```
That gives three files, each larger than 0, and the database copy is `-rw-------` (postgres's own umask through
sudo would otherwise leave it readable by every account, as on the first server). The database copy is **not encrypted**, so it stays in PostgreSQL's own
private folder and is deleted in Set 14.

---

### Set 3. The way back, tested before it is needed

**Server:**
```bash
sudo tar -xzOf /root/pre-rc30/opt-anjal.tgz opt/anjal/webmail/Anjal.Webmail.deps.json | grep -o '"Anjal.Webmail/[^"]*"' | head -1   # "Anjal.Webmail/1.0.0-rc.27"
sudo tar -xzOf /root/pre-rc30/opt-anjal.tgz opt/anjal/server/Anjal.Server.deps.json | grep -o '"Anjal.Server/[^"]*"' | head -1      # "Anjal.Server/1.0.0-rc.27"
sudo tar -tzf /root/pre-rc30/etc.tgz
```
The last command lists both env files, `rclone.conf`, `clamd.conf`, `anjal-portcheck`, the anjal units, and
`anjal-egress.nft`.

**Server** (the database copy restored into a scratch database, counted, then dropped):
```bash
cd /tmp
sudo -u postgres createdb -O anjal anjal_rollback_test
sudo -u postgres pg_restore --exit-on-error -d anjal_rollback_test /var/lib/postgresql/anjal-pre-rc30.dump && echo restored
sudo -u postgres psql -d anjal_rollback_test -Atc "select count(*) from information_schema.tables where table_schema='public'"   # 43
sudo -u postgres psql -d anjal_rollback_test -Atc "select count(*) from messages"
sudo -u postgres psql -d anjal -Atc "select count(*) from messages"
sudo -u postgres dropdb anjal_rollback_test
cd ~
```
Expected: `restored` and `43`. The two message counts are equal, or the live one is a little higher if mail arrived
in between. The way back itself is **Set R** at the end; it uses exactly these copies.

---

### Set 4. TCP 993 in the E2E Security Group (for mail apps)

In the E2E portal, the node's Security Group: read the inbound rules again (Set 0), then add **one** rule: Custom
TCP, port 993, from anywhere. Change nothing else. Until Set 9 nothing listens there, so an open port answers
nothing.

(To keep IMAP off for now, skip this set and leave out `ANJAL_IMAP_TLS_PORT` in Set 6. IMAP on 993 was part of the
rc.27-to-rc.29 install agreed on 12 Nov.)

---

### Set 5. The install, and the database once

**Server:**
```bash
cd ~
sha256sum anjal-1.0.0-rc.30.1.tar.gz | cut -c1-16        # the PC's 16 characters, in lower case
```
If they differ, stop.

**Server:**
```bash
cd ~
tar -xzf anjal-1.0.0-rc.30.1.tar.gz
sudo bash ./anjal-1.0.0-rc.30.1/bin/install.sh ~/anjal-1.0.0-rc.30.1
id anjal-viewer                                          # uid=...(anjal-viewer) gid=...(anjal-viewer)
id -nG anjal | grep -cw anjal-viewer                     # 1
systemctl is-enabled anjal-viewer                        # enabled
systemctl is-active anjal-server anjal-webmail           # active, active (still rc.27 in memory until Set 9)
grep -o '"Anjal.Webmail/[^"]*"' /opt/anjal/webmail/Anjal.Webmail.deps.json | head -1   # "Anjal.Webmail/1.0.0-rc.30.1"
ls /opt/anjal                                            # bin  server  server.old  viewer  webmail  webmail.old
```
`install.sh` keeps rc.27's programs as `server.old` and `webmail.old`, adds the `anjal-viewer` user, folder and unit,
and never touches `/etc/anjal/*.env`. **Run it only once**: a second run would replace the `.old` copies with rc.30.1
(Set 2's copy still has rc.27).

Its closing lines are written for a new server and do not apply here: "backup timer left off until DEPLOY.md
section 11" means it did not *turn on* the timer, and an existing one stays on (check:
`systemctl is-enabled anjal-backup.timer` says `enabled`); "Next: edit /etc/anjal/*.env, then section 6" is
skipped, because Set 6 adds the only two new settings.

**Server** (the schema; it asks for the database password: type it from the paper copy):
```bash
psql "host=127.0.0.1 dbname=anjal user=anjal" -v ON_ERROR_STOP=1 -f ~/anjal-1.0.0-rc.30.1/bin/schema.sql
```
`already exists, skipping` notices are expected. It must end without `ERROR`.

**Server:**
```bash
cd /tmp && sudo -u postgres psql -d anjal -Atc "select count(*) from information_schema.tables where table_schema='public'"; cd ~   # 53
```
These are rc.28's ten new tables (`secure_links`, `secure_link_codes`, `app_passwords`, `imap_folders`, `imap_uids`,
`import_jobs`, `chat_rooms`, `chat_members`, `chat_messages`, `presence`) and the two evidence columns
(`resealed_from_sha256`, `resealed_at`). rc.29 and rc.30 add no table. Everything is added, nothing is changed or
removed, so rc.27 can still run on this database (Set R).

---

### Set 6. The settings that are new since rc.27

| Setting | File | Value here | Why |
|---|---|---|---|
| `ANJAL_IMAP_TLS_PORT` | server.env | **993** (added) | rc.28: IMAP for mail apps. Unset = IMAP off |
| `ANJAL_VIEWER_SOCKET` | webmail.env | **/run/anjal-viewer/viewer.sock** (added) | rc.29: where the viewer listens. Unset = attachments Download only |
| `ANJAL_VIEWER` | webmail.env | unset (= on) | `off` would turn viewing off |
| `ANJAL_VIEWER_DICOM` | webmail.env | **unset (= off)** | rc.30: DICOM stays off until SIAARA v0.1.0 (D-372) |
| `ANJAL_VIEWER_PATH` | webmail.env | unset | development only |
| `ANJAL_VIEWER_TEST_HOOKS` | the viewer | unset, **never set** | tests only |
| Redact, and page changes on files with hidden layers | - | **off, with no setting** | held in the code until the Chuvadi patch packages (3.35.1, or a combined 3.36.0) are in a release (D-405, D-406) |

**Server** (read):
```bash
sudo grep -c '^ANJAL_IMAP_TLS_PORT=' /etc/anjal/server.env                              # 0: not there
sudo grep -c '^ANJAL_VIEWER_SOCKET=' /etc/anjal/webmail.env                             # 0: not there
sudo grep -cE '^ANJAL_VIEWER(_DICOM|_PATH|_TEST_HOOKS)?=' /etc/anjal/webmail.env        # 0: none of them
```
Each 0 means unset. Set 0 showed none of them in the database either. Add only what shows 0:

**Server** (write, then show the new lines):
```bash
echo 'ANJAL_IMAP_TLS_PORT=993' | sudo tee -a /etc/anjal/server.env >/dev/null
echo 'ANJAL_VIEWER_SOCKET=/run/anjal-viewer/viewer.sock' | sudo tee -a /etc/anjal/webmail.env >/dev/null
sudo grep -HE '^ANJAL_IMAP_TLS_PORT=|^ANJAL_VIEWER' /etc/anjal/server.env /etc/anjal/webmail.env
```
Exactly two lines: `server.env:ANJAL_IMAP_TLS_PORT=993` and
`webmail.env:ANJAL_VIEWER_SOCKET=/run/anjal-viewer/viewer.sock`. A setting only in the file is used, and the
start-up log says so.

---

### Set 7. ClamAV: the stream limit, and reloading in place (M2)

**Server** (read):
```bash
sudo grep -nE '^(StreamMaxLength|ConcurrentDatabaseReload)' /etc/clamav/clamd.conf; echo "lines above"
```
- Both `StreamMaxLength 25M` and `ConcurrentDatabaseReload no` already: change nothing, go to Set 8.
- Otherwise, back up the file once:

**Server:**
```bash
sudo cp -p /etc/clamav/clamd.conf /etc/clamav/clamd.conf.rc27
```
**Server**, only if StreamMaxLength is not `25M` (AC-18: matches the 25 MB mail limit):
```bash
sudo sed -i '/^StreamMaxLength/d' /etc/clamav/clamd.conf
echo 'StreamMaxLength 25M' | sudo tee -a /etc/clamav/clamd.conf >/dev/null
```
**Server** (M2), unless the read showed `ConcurrentDatabaseReload no` already:
```bash
sudo sed -i '/^ConcurrentDatabaseReload/d' /etc/clamav/clamd.conf
echo 'ConcurrentDatabaseReload no' | sudo tee -a /etc/clamav/clamd.conf >/dev/null
```
**Server** (show, then restart):
```bash
sudo grep -nE '^(StreamMaxLength|ConcurrentDatabaseReload)' /etc/clamav/clamd.conf
sudo systemctl restart clamav-daemon
```
Expected: `StreamMaxLength 25M` and `ConcurrentDatabaseReload no`. clamd takes about a minute to load its
signatures. Mail with files is answered "try later" meanwhile (D-242). clamd writes its own log,
`/var/log/clamav/clamav.log`, not the journal; its last start-up line is "Self checking every 3600 seconds".

**Server**, after a minute:
```bash
printf 'nPING\n' | sudo -u anjal nc -U /var/run/clamav/clamd.ctl     # PONG
```
If it says the connection was refused, wait another minute and run it again. (A `z` command such as `zPING` must end
with a zero byte, so `echo zPING` only gets "COMMAND READ TIMED OUT"; `VERSION` answers "COMMAND UNAVAILABLE" on
this clamd.)

(`dpkg-reconfigure clamav-daemon` would rewrite this file. If it is ever run, check these lines afterwards.)

---

### Set 8. The outbound firewall, regenerated with rc.28's rules

rc.28's script adds three things: the replies from 993, the `anjal` account's IMAP out to other mail servers (the
mail import), and WireGuard's replies. It also installs the port check that knows 993.

**Server:**
```bash
sudo bash /opt/anjal/bin/anjal-firewall.sh generate
sudo bash /opt/anjal/bin/anjal-firewall.sh apply
ip4=$(getent ahostsv4 gmail-smtp-in.l.google.com | awk 'NR==1{print $1}'); echo "testing $ip4"
timeout 5 bash -c "exec 3<>/dev/tcp/$ip4/25" && echo WRONG || echo "refused (correct)"
sudo journalctl -k --since -2min --no-pager | grep -c 'anjal-egress SMTP-DENY'                    # 1 or more
```
The test goes over IPv4 on purpose: the name resolves to IPv6 first, the server has no IPv6 route, and that fails
with "Network is unreachable" without ever reaching the rule. Expected: "Connection refused", "refused (correct)",
and a count of 1 or more (the rule's own log line). The rules are active now and **remove themselves in 3 minutes** unless confirmed.

**PC, in a new PowerShell window, within the 3 minutes:**
```powershell
ssh -i C:\Users\aruns\.ssh\anjal_e2e arun@151.185.43.171 "echo new session ok"
```
It prints `new session ok`. Then open https://mail.anjal.co.in in the browser: the sign-in page must load.

**Server**, only if both worked:
```bash
sudo bash /opt/anjal/bin/anjal-firewall.sh confirm
sudo bash /opt/anjal/bin/anjal-firewall.sh status | head -30
```
`confirmed - loaded at every boot; port check every 5 minutes`.

---

### Set 9. The viewer started, then the restart (the 30 seconds)

**Server** (M1: read, write, read):
```bash
systemctl cat anjal-viewer | grep '^MemoryMax'                    # MemoryMax=2048M (as shipped)
sudo install -d -m 755 /etc/systemd/system/anjal-viewer.service.d
printf '[Service]\n# This server only (Decision M1, 25 Nov 2026): rc.29 cap while DICOM is off.\n# Remove this file before ANJAL_VIEWER_DICOM=on (the grouping job needs 2048M).\nMemoryMax=1536M\n' | sudo tee /etc/systemd/system/anjal-viewer.service.d/e2e-memory.conf >/dev/null
sudo systemctl daemon-reload
systemctl show anjal-viewer -p MemoryMax --value                 # 1610612736 (1536M)
```

**Server:**
```bash
sudo systemctl start anjal-viewer
sleep 3; sudo test -S /run/anjal-viewer/viewer.sock && echo socket     # socket
sudo systemctl restart anjal-server anjal-webmail
sleep 15; systemctl is-active anjal-viewer anjal-server anjal-webmail   # active, three times
curl -s http://127.0.0.1:8025/healthz | jq -c .                         # "status":"ok"; store, maildir, tls ok
```
If any of the three is not `active`, or health is not ok: Set R.

---

### Set 10. The health check

**Server:**
```bash
grep -o '"Anjal.Server/[^"]*"' /opt/anjal/server/Anjal.Server.deps.json | head -1      # "Anjal.Server/1.0.0-rc.30.1"
sudo journalctl -u anjal-server -u anjal-webmail --since -5min --no-pager | grep -E 'Anjal IMAP|Viewer:|HTTP/3:|Settings:|Outbound worker started|Virus scanning|MTA-STS'
sudo journalctl -u anjal-server -u anjal-webmail -u anjal-viewer --since -5min --no-pager -p warning | tail -20
```
The first log command should show:
- `Anjal IMAP (TLS, port 993) listening`;
- `Viewer: the anjal-viewer service at /run/anjal-viewer/viewer.sock.`;
- `HTTP/3: on (QUIC on UDP port 443; ...)`;
- `Settings: ... applied from the database`, once for each service;
- `Outbound worker started (...)`;
- `Virus scanning: ClamAV at ...` (not `off`);
- the MTA-STS line, if 13i was done.

In the warnings, nothing new except, possibly, the start-up note that `ANJAL_IMAP_TLS_PORT` and
`ANJAL_VIEWER_SOCKET` come from the file only. Send anything else.

**Server** (the viewer, as the webmail's user; it reads only made-up files of its own):
```bash
sudo -u anjal python3 /opt/anjal/bin/viewer_check.py /run/anjal-viewer/viewer.sock     # viewer ok: 1 page, 2 locked archives, DICOM
systemctl show anjal-viewer -p NRestarts -p PrivateNetwork -p RestrictAddressFamilies -p MemoryMax
pgrep -u anjal-viewer -f -- '--child' | wc -l                                           # 3 (2 ready, 1 waiting to join them)
```
Expected: `NRestarts=0`, `PrivateNetwork=yes`, `RestrictAddressFamilies=AF_UNIX`, and `MemoryMax=1610612736` (1536M, M1). (The check previews DICOM by talking to the viewer directly. In the webmail, DICOM
stays off.)

**Server** (memory now, beside Set 0's numbers):
```bash
free -m
for u in anjal-server anjal-webmail anjal-viewer postgresql@18-main clamav-daemon; do printf '%-22s %s\n' $u "$(systemctl show -p MemoryCurrent --value $u)"; done
```
Write these down beside Set 0's. Then the headroom again, now with everything running. The viewer already uses
part of its 1536M, so only what it can still grow by counts:

**Server** (read only):
```bash
avail=$(free -m | awk '/^Mem:/ {print $7}')
viewer=$(( $(systemctl show -p MemoryCurrent --value anjal-viewer) / 1048576 ))
echo "available ${avail} MB, viewer now ${viewer} MB"
echo "headroom with M1 + M2: $(( avail - (1536 - viewer) - 300 )) MB    (needs at least 500)"
```
If it is under 500 MB, say so before going on.

---

### Set 11. The ports after

**Server:**
```bash
sudo ss -tlnp
sudo ss -ulnp
sudo test -S /run/anjal-viewer/viewer.sock && echo socket     # socket
sudo /usr/local/sbin/anjal-portcheck                # anjal-portcheck: ok - Anjal's ports are held only by Anjal
```
- **TCP:** exactly Set 0's list **plus 993, held by Anjal.Server**, on the same addresses as 465.
- **UDP:** unchanged.
- The viewer adds no network port at all, only its socket file. (It runs in a network namespace of its own,
  `PrivateNetwork=yes`, so `ss` on the host does not list that socket; `test -S` sees the file.)

---

### Set 12. A smoke test with real mail

**PC:**
```powershell
curl.exe -sI https://mail.anjal.co.in/sign-in | Select-String "^HTTP"                       # HTTP/1.1 200 OK
curl.exe -sI http://mail.anjal.co.in/ | Select-String "^HTTP|^Location"                    # a 3xx, Location: https://mail.anjal.co.in/
(Test-NetConnection mail.anjal.co.in -Port 993).TcpTestSucceeded                            # True
```

**In: from Yahoo** (arunshiva_b@yahoo.com), send one mail to arun@anjal.co.in with four small files attached: a PDF, a
Word file, an Excel file and a ZIP (all four under 25 MB together). Then:

**Server:**
```bash
sudo journalctl -u anjal-server --since -10min --no-pager | grep -E 'Greylist:|Spam: score|Virus|Delivered'
```
Expected:
- a `Greylist:` line (`not delayed`: Yahoo passes SPF);
- `Spam: score ...`;
- no virus found;
- `Delivered arun@anjal.co.in -> ...`.

**In the webmail** (signed in as arun@):
1. Open that mail. Each of the four files opens in the mail window, and **Download** still gives the file as it came.
2. A ZIP lists its files, and a program inside one is listed but cannot be opened.
3. A PDF's **Edit** opens the editor. **Redact is not offered** (held, D-405).
4. **Out:** reply to the Yahoo address. In Yahoo, open it → More → View raw message: `Authentication-Results` shows
   `spf=pass`, `dkim=pass` (anjal.co.in) and `dmarc=pass`.
5. Optional, on your phone: Settings › Mail apps › Add a mail app. With the password shown there, your phone's mail
   app shows the Inbox, and a mail sent from the phone arrives, with its copy in Sent once.

**Server** (the evidence store is whole):
```bash
TOKEN=$(sudo grep '^ANJAL_API_TOKEN=' /etc/anjal/server.env | cut -d= -f2-)
curl -s -X POST -d '' -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/evidence/verify | jq -c '{intact, problems}'   # {"intact":true,"problems":[]}
unset TOKEN
```

If everything above passed, the upgrade is good. **Decide now whether to stay on rc.30.1.** The next set is the line.

---

### Set 13. The re-seal of evidence kept before rc.25 (once)

rc.28 (D-264) seals the originals of organisations' mail kept before rc.25, and anything still queued from then, as
everything since rc.25 already is. The old files go on that day's purge list. Up to this point, Set R puts rc.27
back exactly as it was. After this set, going back to rc.27 has not been tested against re-sealed evidence, so treat
this set as the line.

**Server** (a dry run; it changes nothing and prints one line):
```bash
sudo systemd-run --uid=anjal --pty --wait -p EnvironmentFile=/etc/anjal/server.env -p WorkingDirectory=/opt/anjal/server /opt/anjal/server/Anjal.Server --reseal-old --dry-run
```
Read the line: `... checked; N of organisations to seal, ... 0 with a problem; Q queued message(s) to seal.` More
than 0 with a problem: stop and send it (DEPLOY.md 13g).

**Server** (for real):
```bash
sudo systemd-run --uid=anjal --pty --wait -p EnvironmentFile=/etc/anjal/server.env -p WorkingDirectory=/opt/anjal/server /opt/anjal/server/Anjal.Server --reseal-old
```
It prints the SHA-256 of `manifests/reseal-YYYYMMDD-HHmmss.txt`. Write it on the upgrade form. A second run finds
nothing to do.

**Server:**
```bash
cd /tmp && sudo -u postgres psql -d anjal -Atc "select count(*) filter (where resealed_at is not null), count(*) from evidence"; cd ~
TOKEN=$(sudo grep '^ANJAL_API_TOKEN=' /etc/anjal/server.env | cut -d= -f2-)
curl -s -X POST -d '' -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/evidence/verify | jq -c '{intact, problems}'   # {"intact":true,"problems":[]}
unset TOKEN
```

---

### Set 14. The next day, then cleanup

**Server** (about 24 hours later):
```bash
systemctl is-active anjal-viewer anjal-server anjal-webmail                     # active, three times
systemctl show anjal-viewer -p NRestarts --value                                # 0
sudo journalctl -k --since -1d --no-pager | grep -ciE 'out of memory|oom-kill'  # 0
sudo journalctl -u anjal-backup --since -1d --no-pager | grep -E "verify ok|done|FAIL|ERROR"
free -m
```
The night's backup shows `verify ok` and `done`. If there was any out-of-memory line, send
`sudo journalctl -k --since -1d | grep -iE -A3 'out of memory|oom-kill'` before anything else.

**Server**, a week later if all is well (the copies are no longer needed; the database copy is not encrypted):
```bash
sudo rm -f /var/lib/postgresql/anjal-pre-rc30.dump
sudo rm -rf /root/pre-rc30
rm -rf ~/anjal-1.0.0-rc.30.1 ~/anjal-1.0.0-rc.30.1.tar.gz
sudo ls /root /var/lib/postgresql
```

**PC:**
```powershell
Remove-Item C:\Users\aruns\Documents\Anjal\anjal-scaffold\anjal\artifacts\anjal-1.0.0-rc.30.1, C:\Users\aruns\Documents\Anjal\anjal-scaffold\anjal\artifacts\anjal-1.0.0-rc.30.1.tar.gz -Recurse -Force -ErrorAction SilentlyContinue
```

---

### Set R. Back to rc.27 (only if Sets 9 to 12 fail; tested in Set 3)

This set puts back rc.27's programs, units, settings, firewall and clamd file. **The database stays as it is**:
rc.30.1 only added tables and columns, which rc.27 ignores, and restoring the copy would lose the records of any
mail that arrived since Set 2. Restore the database copy only if the database itself is damaged (last block).

**Server:**
```bash
sudo systemctl stop anjal-webmail anjal-server anjal-viewer
sudo systemctl disable anjal-viewer
sudo rm -rf /opt/anjal/server /opt/anjal/webmail /opt/anjal/viewer /opt/anjal/server.old /opt/anjal/webmail.old /opt/anjal/bin
sudo tar -xzf /root/pre-rc30/opt-anjal.tgz -C /
sudo rm -f /etc/systemd/system/anjal-viewer.service
sudo rm -rf /etc/systemd/system/anjal-viewer.service.d
sudo tar -xzf /root/pre-rc30/etc.tgz -C /
sudo systemctl daemon-reload
sudo systemctl restart anjal-egress
grep -o '"Anjal.Webmail/[^"]*"' /opt/anjal/webmail/Anjal.Webmail.deps.json | head -1   # "Anjal.Webmail/1.0.0-rc.27"
sudo grep -cE '^ANJAL_(IMAP_TLS_PORT|VIEWER_SOCKET)=' /etc/anjal/server.env /etc/anjal/webmail.env   # 0 and 0
```

**Server**, only if Set 7 changed clamd.conf:
```bash
sudo systemctl restart clamav-daemon
```
**Server:**
```bash
sudo systemctl start anjal-server anjal-webmail
sleep 15; systemctl is-active anjal-server anjal-webmail       # active, active
curl -s http://127.0.0.1:8025/healthz | jq -c .                 # "status":"ok"
sudo ss -tlnp
```
The ports are Set 0's list again. Then, at E2E, remove the TCP 993 rule. The `anjal-viewer` user may stay; it holds
nothing. Send Claude the output of the set that failed.

**Server**, only if the database itself is damaged (this loses the records of mail received since Set 2; the mail
files stay in the Maildirs):
```bash
sudo systemctl stop anjal-webmail anjal-server
cd /tmp && sudo -u postgres pg_restore --clean --if-exists --exit-on-error -d anjal /var/lib/postgresql/anjal-pre-rc30.dump && echo restored; cd ~
sudo systemctl start anjal-server anjal-webmail
```

---

## Part B - the operator steps agreed for Sangam's mail (separate; not needed for the upgrade)

Decided by the owner on 13 Nov 2026: the 13 Nov answers' items M4, M5 and M10, and MTA-STS for sangamid.in. (These
M-numbers are the 13 Nov answers', not Decision M's.) Do them after Part A, but not in
the same sitting as Set 13. Every step reads first.

Sangam runs on its own VM (the owner, 25 Nov 2026). Everything below is on Anjal's side and works wherever Sangam
runs, because Sangam reaches Anjal at https://mail.anjal.co.in.

### B0. Read what is there

**PC:**
```powershell
nslookup -type=TXT _spf.anjal.co.in 8.8.8.8
nslookup -type=TXT sangamid.in 8.8.8.8
nslookup -type=MX sangamid.in 8.8.8.8
nslookup -type=TXT _dmarc.sangamid.in 8.8.8.8
nslookup -type=TXT anjal._domainkey.sangamid.in 8.8.8.8
nslookup -type=A mta-sts.sangamid.in 8.8.8.8
nslookup -type=TXT _mta-sts.sangamid.in 8.8.8.8
nslookup -type=TXT _smtp._tls.sangamid.in 8.8.8.8
```
Write down every answer. Then sign in to Anjal as an operator:
- **Anjal console › Organisations:** is `sangamid` there, and with what "Messages sent a day"?
- **Signed in as sangamid's administrator:**
  - Organisation › Domains: is sangamid.in proved?
  - Organisation › Sending systems: are the applications `Sangam` and `Sangam staging` there?

If the organisation, its proved domain or the `Sangam` application is missing, do those first, as agreed on 13 Nov
(OPS-14 and the 13 Nov answers, §3 steps 2, 3, 5 and 6): the organisation with Arun as its administrator; the domain
proved with its `_anjal` TXT record, which makes the DKIM key `anjal`; the addresses grievance@, dmarc-reports@ and
tls-reports@; then the application `Sangam`, sending as `no-reply@sangamid.in`. The live key is shown once: Arun puts
it in Sangam's secret file himself, and it never goes into a chat.

### B1. `_spf.anjal.co.in`, the shared SPF record (M10 b)

B0 must have answered "Non-existent domain" for `_spf.anjal.co.in`. If it did, add at GoDaddy, for **anjal.co.in**:
TXT, host `_spf`, value `v=spf1 ip4:151.185.43.171 -all`.

**PC**, until it answers with the value:
```powershell
nslookup -type=TXT _spf.anjal.co.in 8.8.8.8      # "v=spf1 ip4:151.185.43.171 -all"
```

### B2. sangamid.in's SPF through it

From B0, sangamid.in's current SPF record (the TXT starting `v=spf1`): there must be only one. At GoDaddy, for
**sangamid.in**, **edit that record**, never add a second: TXT `@`, value `v=spf1 include:_spf.anjal.co.in -all`.
(Leave any other TXT records at `@`, such as `_anjal`'s proof or a site verification, as they are.)

**PC:**
```powershell
nslookup -type=TXT sangamid.in 8.8.8.8          # one v=spf1 line: "v=spf1 include:_spf.anjal.co.in -all"
```
On Organisation › Domains, after **Check**, SPF says **pass**. rc.30.1 accepts the include; up to rc.30 it said
"different", and the operators' digest would have flagged it every day.

DMARC for sangamid.in, as decided: the value Organisation › Domains shows for DMARC, with `p=none` in place of
`p=reject` for the first two weeks, then `p=reject` as shown (strict alignment, subdomains too, reports to
sangamid.in's own dmarc-reports address). Read `_dmarc` from B0 first, and edit the record, never add a second.

### B3. The sangamid organisation: 20,000 a day (M4, operator)

Anjal console › Organisations › sangamid › **Change limits**. Read **Messages sent a day** (2,000 by default), change
only that field to **20000**, then **Save limits**. The change is recorded as `ops.org.limits`.

### B4. The `Sangam` application's limits (M4, the organisation's administrator)

Organisation › Sending systems › Sangam › **Limits**:
1. Read **Sends an hour** (200 by default) and **Sends a day** (1,000 by default).
2. Set them to **2000** and **20000**, the ceilings. Anjal also allows at most 60 a minute, fixed in the code.
3. **Allowed from** stays **empty** for now. After Sangam's first send, the organisation's activity log shows "An
   application key was used from a new address" with the address. Enter that address here then, and Save.

**Signing:** "A key is enough" for the pilot (M1 a). "Signing required" comes with Sangam rc.17, before any customer.

### B5. The `Sangam staging` application (M5)

1. Organisation › Sending systems › **Add an application**: `Sangam staging`, sending as `no-reply@sangamid.in`.
2. **Limits:** low. **Suggested: 50 an hour and 200 a day; the owner's call.** Allowed from empty until its first
   send, as in B4.
3. **Keys** › **New HTTP key**: its own live key, shown once, for Sangam's staging secret file.
4. **Keys** › tick **Sandbox key: sends nothing** › **New HTTP key**: an `ak_test_` key for Sangam's automated tests.
   It runs every step but delivers nothing.

### B6. MTA-STS for sangamid.in

The policy is served by Anjal at https://mta-sts.sangamid.in/.well-known/mta-sts.txt, built from its settings, so
the name must be on Anjal's certificate. (The certificate is public in Certificate Transparency logs, so this shows
that sangamid.in's mail is at Anjal. That is normal for hosted mail.) The order matters: the policy is served and
checked **before** the `_mta-sts` record tells anyone to look.

1. **DNS first.** B0 must have shown no `mta-sts.sangamid.in`. At GoDaddy, for sangamid.in: A record, host
   `mta-sts`, value `151.185.43.171`.

   **PC**, until it answers:
   ```powershell
   nslookup -type=A mta-sts.sangamid.in 8.8.8.8     # 151.185.43.171
   ```
   (If this name cannot be reached when Anjal renews, the whole certificate fails to renew, mail.anjal.co.in
   included. Keep this A record as long as the name is in the setting below.)

2. **Read the three settings** (both scopes for the certificate):

   **Server:**
   ```bash
   TOKEN=$(sudo grep '^ANJAL_API_TOKEN=' /etc/anjal/server.env | cut -d= -f2-)
   curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/settings | jq -r '(.server + .webmail)[] | select(.key | test("ACME_DOMAINS|MTA_STS")) | "\(.scope) \(.key)=\(.value)"'
   unset TOKEN
   ```
   Write down `ANJAL_ACME_DOMAINS`; it must be the same in `server` and `webmail`. Write down
   `ANJAL_MTA_STS_DOMAINS` (unset means the default, `anjal.co.in`) and `ANJAL_MTA_STS_MODE`. The mode is one
   setting for every domain, so sangamid.in gets the same mode as anjal.co.in.

3. **Write**, showing the old and the new value first. Each value is built from the one just read, adding the new
   name only if it is not there yet:

   **Server:**
   ```bash
   TOKEN=$(sudo grep '^ANJAL_API_TOKEN=' /etc/anjal/server.env | cut -d= -f2-)
   OLD=$(curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/settings | jq -r '.webmail[] | select(.key=="ANJAL_ACME_DOMAINS") | .value')
   [ -z "$OLD" ] && OLD=$(sudo grep '^ANJAL_ACME_DOMAINS=' /etc/anjal/webmail.env | cut -d= -f2-)
   NEW=$(echo "$OLD" | grep -qw 'mta-sts.sangamid.in' && echo "$OLD" || echo "$OLD,mta-sts.sangamid.in")
   OLDS=$(curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/settings | jq -r '.webmail[] | select(.key=="ANJAL_MTA_STS_DOMAINS") | .value')
   [ -z "$OLDS" ] && OLDS=anjal.co.in
   NEWS=$(echo "$OLDS" | grep -qw 'sangamid.in' && echo "$OLDS" || echo "$OLDS,sangamid.in")
   echo "ANJAL_ACME_DOMAINS:    $OLD  ->  $NEW"
   echo "ANJAL_MTA_STS_DOMAINS: $OLDS  ->  $NEWS"
   ```
   Check the two lines. `NEW` must be the old list plus `,mta-sts.sangamid.in`, and `NEWS` must be the old list (or
   `anjal.co.in`) plus `,sangamid.in`. If either looks wrong, stop. Otherwise, in the **same window**:

   **Server:**
   ```bash
   for scope in server webmail; do curl -s -X PUT -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "{\"value\":\"$NEW\"}" http://127.0.0.1:8025/api/settings/$scope/ANJAL_ACME_DOMAINS | jq -c '{scope, key, value}'; done
   curl -s -X PUT -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "{\"value\":\"$NEWS\"}" http://127.0.0.1:8025/api/settings/webmail/ANJAL_MTA_STS_DOMAINS | jq -c '{scope, key, value}'
   unset TOKEN OLD NEW OLDS NEWS
   start=$(date '+%Y-%m-%d %H:%M:%S')
   sudo systemctl restart anjal-webmail anjal-server
   sudo -u anjal /opt/anjal/server/Anjal.Server --acme-renew-now
   ```

4. **After a minute, in the same window:**

   **Server:**
   ```bash
   sudo journalctl -u anjal-webmail --since "$start" --no-pager | grep -E "ACME|MTA-STS"
   echo | openssl s_client -connect 127.0.0.1:443 -servername mta-sts.sangamid.in 2>/dev/null | openssl x509 -noout -ext subjectAltName
   ```
   Expected:
   - in the log, the certificate issued, and `MTA-STS: <mode> (mx mail.anjal.co.in, ...)` listing both
     `https://mta-sts.anjal.co.in/...` and `https://mta-sts.sangamid.in/...`;
   - the certificate's names include `mta-sts.sangamid.in`.

   If the log shows an ACME error, the old certificate stays in use (it is valid for weeks). Send the log.

5. **From outside, exactly as a sending server fetches it:**

   **PC:**
   ```powershell
   curl.exe -s https://mta-sts.sangamid.in/.well-known/mta-sts.txt
   ```
   Four lines: `version: STSv1`, `mode: <the mode>`, `mx: mail.anjal.co.in` and `max_age: ...`, with no
   certificate warning.

6. **Only now, DNS** at GoDaddy for sangamid.in, read from B0 first and edited if already there:
   - TXT `_mta-sts`: `v=STSv1; id=20261125T000000` (a new id whenever the policy changes);
   - TXT `_smtp._tls`: the value Organisation › Domains shows for TLS reports (reports to sangamid.in's own
     tls-reports address).

   Then, on Organisation › Domains, after **Check**:
   - MTA-STS says pass if the mode is enforce, or testing otherwise;
   - TLS reports says pass.

### B7. Check

- Organisation › Domains for sangamid.in: every line passes (MTA-STS may say testing).
- mxtoolbox: SPF, DKIM (selector `anjal`) and DMARC for sangamid.in.
- Sangam's own check: `scripts/check-mail-dns.ps1 -Domain sangamid.in -DkimSelector anjal -AnjalSpf include:_spf.anjal.co.in`
  (in Sangam's repository; run by its chat).
- One real sign-in code received from Sangam. Then fill in B4's Allowed from.
