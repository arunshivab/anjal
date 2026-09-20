# Anjal deployment runbook - Ubuntu 24.04 on E2E Networks

This is the complete, in-order procedure for taking a fresh VM to a
running Anjal mail server for `anjal.co.in`: OS hardening, PostgreSQL,
the two services, DNS, TLS via the built-in ACME client, the first
tenant and mailbox, first inbound and outbound mail, backups, and a
restore drill. Every step has a check; do not move on until the check
passes. Expect two to three hours end to end, most of it waiting for DNS.

Conventions: commands prefixed `vm$` run on the VM over SSH as the
`ubuntu` user (they use `sudo` where needed); `pc>` runs on the laptop in
PowerShell; `api$` is a `curl` against the admin API on the VM.

Placeholders used throughout - substitute your real values everywhere:

| Placeholder | Meaning |
|---|---|
| `203.0.113.10` | the VM's public IPv4 |
| `2001:db8::10` | the VM's public IPv6, if E2E assigns one |
| `mail.anjal.co.in` | the mail host name (SMTP banner, webmail, certificate) |
| `anjal.co.in` | the mail domain (addresses are `user@anjal.co.in`) |
| `API_TOKEN` | the value of `ANJAL_API_TOKEN` in `/etc/anjal/server.env` |

---

## 0. Before ordering the VM

1. **Decide the size.** For one to three tenants: 2 vCPU, 4 GB RAM,
   80 GB disk is comfortable; mail is I/O-light. Pick a plan with a
   **static public IPv4** - reputation is tied to the IP, and a changing
   IP means re-doing SPF and PTR.
2. **Port 25 outbound.** Most cloud providers block it by default to
   stop spam. Before ordering, confirm with E2E support (ticket) that
   outbound TCP 25 is open on the plan, or can be opened on request. If
   it cannot be opened at all, outbound mail must go through a relay
   (`ANJAL_OUTBOUND_MODE=relay`) - decide that before you start.
3. **PTR (reverse DNS).** Ask E2E how PTR records are set for the IP
   (panel or ticket). You will set it to `mail.anjal.co.in` in section 5.
4. **IP reputation.** Once you have the IP, check it is not on common
   blocklists before building anything on it:
   https://mxtoolbox.com/blacklists.aspx (enter the IP). If it is listed
   on Spamhaus or Barracuda, ask E2E for a different IP now - delisting a
   burned IP is slower than swapping it.
5. **Backblaze B2.** Create a bucket named `anjal-backup` (private,
   default encryption off - rclone encrypts client-side) and an
   application key restricted to that bucket with read/write/delete.
   Keep the key ID and application key for section 9.

---

## 1. Base OS

```
vm$ sudo apt update && sudo apt full-upgrade -y
vm$ sudo hostnamectl set-hostname mail.anjal.co.in
vm$ sudo timedatectl set-timezone UTC          # logs and Maildir names in UTC; the webmail shows local time
vm$ sudo apt install -y unattended-upgrades fail2ban postgresql postgresql-client rclone curl jq netcat-openbsd
vm$ sudo dpkg-reconfigure -plow unattended-upgrades   # choose Yes
```

### 1a. Two firewalls, not one

On E2E there are **two** layers between the internet and Anjal, and a port
must be open in **both** or it is dead:

1. **The Security Group** - E2E's own virtual firewall, configured in the
   MyAccount portal, outside the VM. Nothing reaches the machine unless
   the Security Group allows it.
2. **firewalld** - on the VM itself. E2E's own documentation uses
   firewalld, so this runbook does too. **Do not also install ufw.**
   Two firewalls disagreeing is the most common way to lock yourself out
   of SSH.

**Security Group** (portal → Network → Security Groups → Create):

| Direction | Protocol | Ports | Source / destination | Why |
| --- | --- | --- | --- | --- |
| Inbound | Custom TCP | 22 | My IP (or your office range) | SSH. Leave it open to Any only if your address changes constantly |
| Inbound | Custom TCP | 25 | Any | every mail server on the internet delivers here |
| Inbound | Custom TCP | 80 | Any | ACME HTTP-01 challenge, and the redirect to HTTPS |
| Inbound | Custom TCP | 443 | Any | webmail |
| Inbound | Custom TCP | 587 | Any | your own clients, authenticated |
| Outbound | ALL | - | Any | see the warning below |

**Leave outbound permissive.** Anjal must reach port 25 on other mail
servers, 53 for DNS, and 443 for Let's Encrypt. A tightened outbound rule
that forgets DNS produces a server that looks healthy and silently fails
every delivery and every certificate renewal - a genuinely confusing
afternoon. If you must restrict it, allow at least TCP 25, 53, 80, 443
and UDP 53.

### 1b. firewalld on the VM

```
vm$ sudo apt install -y firewalld
vm$ sudo systemctl enable --now firewalld
vm$ sudo firewall-cmd --add-port=22/tcp --permanent
vm$ sudo firewall-cmd --add-port=25/tcp --permanent
vm$ sudo firewall-cmd --add-port=80/tcp --permanent
vm$ sudo firewall-cmd --add-port=443/tcp --permanent
vm$ sudo firewall-cmd --add-port=587/tcp --permanent
```

Outbound port 25 explicitly, per E2E's instructions (they confirmed it can
be opened on request - do that before this step):

```
vm$ sudo firewall-cmd --permanent --direct --add-rule ipv4 filter OUTPUT 0 -p tcp -m tcp --dport=25 -j ACCEPT
vm$ sudo firewall-cmd --reload
vm$ sudo firewall-cmd --list-all
```

fail2ban protects SSH out of the box; leave the default jail on.

**Check - and do this now, not after installing Anjal:**

```
vm$ sudo firewall-cmd --list-ports          # the five ports above
vm$ nc -vz gmail-smtp-in.l.google.com 25    # must connect
vm$ nc -vz 1.1.1.1 53                       # DNS reachable
vm$ timedatectl                             # UTC, "System clock synchronized: yes"
```

If `nc` to port 25 hangs or is refused, outbound 25 is still blocked -
either the Security Group or E2E's network. Resolve that with E2E before
going further; everything downstream assumes it works. If it cannot be
opened at all, set `ANJAL_OUTBOUND_MODE=relay` in `server.env` and use a
relay: receiving still works normally.

From the laptop, after the Security Group is attached:

```
pc> Test-NetConnection -ComputerName 203.0.113.10 -Port 25
pc> Test-NetConnection -ComputerName 203.0.113.10 -Port 443
```

---

## 2. PostgreSQL

Create the database and a dedicated role. The password goes into both env
files in section 6.

```
vm$ sudo -u postgres psql
postgres=# CREATE ROLE anjal LOGIN PASSWORD 'CHOOSE-A-LONG-RANDOM-PASSWORD';
postgres=# CREATE DATABASE anjal OWNER anjal;
postgres=# \q
```

Apply the schema (the tarball in section 3 contains `bin/schema.sql`;
until then, copy `tools/sql/schema.sql` from the repo):

```
vm$ psql "host=127.0.0.1 dbname=anjal user=anjal password=..." -v ON_ERROR_STOP=1 -f schema.sql
```

**Check:** `vm$ psql "host=127.0.0.1 dbname=anjal user=anjal password=..." -c '\dt'` lists 15 tables including `tenants`, `mailboxes`, `messages`, `sender_rules`.

PostgreSQL listens on 127.0.0.1 only by default; leave it that way.

---

## 3. Build and upload the release

On the laptop, from the repo root at the tagged release:

```
pc> git checkout v0.13.0
pc> .\deploy\publish.ps1 -Version 0.13.0
pc> scp .\artifacts\anjal-0.13.0.tar.gz ubuntu@203.0.113.10:~/
```

`publish.ps1` produces self-contained linux-x64 builds of both
processes (no .NET runtime to install on the VM), plus `bin/` with the
scripts, units, env templates and `schema.sql`.

On the VM:

```
vm$ tar -xzf anjal-0.13.0.tar.gz
vm$ sudo ./anjal-0.13.0/bin/install.sh ~/anjal-0.13.0
```

`install.sh` creates the `anjal` system user, the directory layout below,
installs the binaries under `/opt/anjal`, writes config **templates** to
`/etc/anjal` (it never overwrites an existing config) and installs and
enables the systemd units without starting them.

| Path | Owner / mode | Purpose |
|---|---|---|
| `/opt/anjal/server`, `/opt/anjal/webmail` | root 755 | binaries (previous release kept as `*.old`) |
| `/opt/anjal/bin` | root 755 | `backup.sh`, `restore.sh` |
| `/etc/anjal/server.env`, `webmail.env`, `rclone.conf` | root:anjal 640 | configuration and secrets |
| `/var/mail/anjal` | anjal 700 | Maildirs, one per mailbox |
| `/var/lib/anjal/acme` | anjal 700 | ACME account key, certificate, key, status |
| `/var/lib/anjal/backup` | anjal 700 | scratch for `pg_dump` |

**Check:** `vm$ /opt/anjal/server/Anjal.Server --acme-renew-now` prints "Renewal requested" (proves the binary runs; harmless before configuration - delete the marker: `sudo rm -f /var/lib/anjal/acme/renew.request`).

---

## 4. Configure

Edit both files; each line is explained in `README.md`.

```
vm$ sudo nano /etc/anjal/server.env
vm$ sudo nano /etc/anjal/webmail.env
```

Required changes in `server.env`:

- `ANJAL_POSTGRES` - the password from section 2.
- `ANJAL_API_TOKEN` - `openssl rand -hex 32`.
- Leave `ANJAL_ACME_STAGING` **commented out for now** - section 7 explains the rehearsal.

Required changes in `webmail.env`:

- `ANJAL_POSTGRES` - the same connection string.
- `ANJAL_ACME_STAGING=true` - **uncomment it** for the rehearsal.

Both files must agree on `ANJAL_POSTGRES`, `ANJAL_MAILDIR_ROOT`,
`ANJAL_ACME_DIR` and `ANJAL_ACME_DOMAINS`.

**Check:** `vm$ sudo -u anjal cat /etc/anjal/server.env >/dev/null && echo readable` (the service user can read it); `vm$ ls -l /etc/anjal` shows `-rw-r-----  root anjal` on all three.

---

## 5. DNS - part one (before first start)

At GoDaddy, for `anjal.co.in`:

| Type | Name | Value | TTL |
|---|---|---|---|
| A | `mail` | `203.0.113.10` | 600 |
| AAAA | `mail` | `2001:db8::10` (only if E2E gave you IPv6 **and** it has PTR) | 600 |
| MX | `@` | `10 mail.anjal.co.in` | 600 |

Leave SPF as `v=spf1 -all` and DMARC as it is for now (nothing sends yet).

At E2E: set the **PTR** of `203.0.113.10` to `mail.anjal.co.in`.

Wait for propagation, then check from the laptop:

```
pc> nslookup -type=A mail.anjal.co.in 8.8.8.8
pc> nslookup -type=MX anjal.co.in 8.8.8.8
pc> nslookup 203.0.113.10 8.8.8.8          # PTR must answer mail.anjal.co.in
```

**Check:** all three answer correctly from a public resolver. The PTR is
the one people forget; several large providers refuse mail from IPs
whose PTR does not match the HELO name.

---

## 6. First start

```
vm$ sudo systemctl start anjal-server
vm$ sudo journalctl -u anjal-server -n 40 --no-pager
```

Expected lines: `Using PostgreSQL store.`, `Anjal SMTP (MTA, port 25)`,
`Anjal SMTP (Submission, port 587) listening`, `TLS: ACME configured for
mail.anjal.co.in but no certificate ... yet`, `API listening on
http://127.0.0.1:8025`, `Health at ...`.

```
vm$ sudo systemctl start anjal-webmail
vm$ sudo journalctl -u anjal-webmail -n 40 -f
```

Within a minute you should see `ACME: renewal service for
mail.anjal.co.in via https://acme-staging-v02...`, then `ACME: account:
...`, `ACME: order ...`, `ACME: mail.anjal.co.in validated.`, `ACME:
certificate issued, expires ...`. If instead you see a challenge
failure, the usual causes are: DNS not propagated yet (section 5), port
80 not open (section 1), or the PTR/A mismatch. Fix and the service
retries automatically with backoff; `sudo systemctl restart anjal-webmail`
retries immediately.

**Check:**

```
api$ curl -s http://127.0.0.1:8025/healthz | jq .
```

`status` is `ok` once the (staging) certificate exists; components
`store`, `maildir`, `tls` all `ok`. And:

```
api$ curl -s -H "Authorization: Bearer API_TOKEN" http://127.0.0.1:8025/api/acme | jq .
```

shows `hasCertificate: true`, `daysRemaining` around 89, and
`acmeDirectoryUrl` containing `staging`.

From the laptop, `https://mail.anjal.co.in/` will show a **certificate
warning** (staging certificates are deliberately untrusted); accept it
and confirm the login page renders. `http://mail.anjal.co.in/` must
redirect to HTTPS.

---

## 7. ACME rehearsal, then production

The point of staging is to prove the whole issue-renew-reload path with
no rate-limit risk. Force a renewal and watch it complete:

```
api$ curl -s -X POST -H "Authorization: Bearer API_TOKEN" http://127.0.0.1:8025/api/acme/renew
vm$ sudo journalctl -u anjal-webmail -f     # expect "renew-now request found" then "certificate issued"
api$ curl -s -H "Authorization: Bearer API_TOKEN" http://127.0.0.1:8025/api/acme | jq .issuedAt
```

`issuedAt` must have changed. Then confirm both processes picked up the
new certificate without a restart:

```
vm$ openssl s_client -connect 127.0.0.1:587 -starttls smtp -servername mail.anjal.co.in </dev/null 2>/dev/null | openssl x509 -noout -dates
vm$ openssl s_client -connect 127.0.0.1:443 -servername mail.anjal.co.in </dev/null 2>/dev/null | openssl x509 -noout -dates
```

Both show the new `notBefore`. **Now switch to production:**

```
vm$ sudo systemctl stop anjal-webmail anjal-server
vm$ sudo rm -rf /var/lib/anjal/acme/*         # staging account and certificate are not valid in production
vm$ sudo sed -i 's/^ANJAL_ACME_STAGING=true/# ANJAL_ACME_STAGING=true/' /etc/anjal/webmail.env
vm$ sudo systemctl start anjal-server anjal-webmail
vm$ sudo journalctl -u anjal-webmail -f       # wait for "certificate issued" from acme-v02.api.letsencrypt.org
```

Force **one** more renewal exactly as above to prove production
renewal works, then leave it alone. Let's Encrypt allows 5 duplicate
certificates per week - do not loop this.

**Check:** the browser shows a valid padlock on `https://mail.anjal.co.in/`;
`/api/acme` shows `acmeDirectoryUrl` without `staging`;
https://www.ssllabs.com/ssltest/analyze.html?d=mail.anjal.co.in grades A.

Renewal is now automatic: the service checks hourly and renews at 30
days remaining. Mark the calendar for **60 days from today** and confirm
on that day that `issuedAt` has moved and `/healthz` `tls` is `ok`.
Until that has happened once, keep the tenant count at three or fewer.

---

## 8. First tenant, domain, DKIM and mailbox

```
api$ H='-H "Authorization: Bearer API_TOKEN" -H "Content-Type: application/json"'
api$ curl -s -X POST -H "Authorization: Bearer API_TOKEN" -H "Content-Type: application/json" \
       http://127.0.0.1:8025/api/tenants -d '{"slug":"imagiqa","displayName":"imagiQa"}' | jq .
api$ curl -s -X POST -H "Authorization: Bearer API_TOKEN" -H "Content-Type: application/json" \
       http://127.0.0.1:8025/api/tenant-domains -d '{"tenantSlug":"imagiqa","domain":"anjal.co.in"}' | jq .
```

DKIM: generate a 2048-bit RSA key on the VM, upload the private key,
publish the public key.

```
vm$ openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out /tmp/dkim.pem
vm$ openssl pkey -in /tmp/dkim.pem -pubout -outform DER | base64 -w0 > /tmp/dkim.pub.b64
vm$ jq -n --arg pem "$(cat /tmp/dkim.pem)" '{domain:"anjal.co.in",selector:"default",privateKeyPem:$pem}' > /tmp/dkim.json
api$ curl -s -X POST -H "Authorization: Bearer API_TOKEN" -H "Content-Type: application/json" \
       http://127.0.0.1:8025/api/dkim-keys -d @/tmp/dkim.json | jq .
vm$ echo "v=DKIM1; k=rsa; p=$(cat /tmp/dkim.pub.b64)"      # the DNS TXT value
vm$ shred -u /tmp/dkim.pem /tmp/dkim.json
```

The mailbox (password is hashed server-side; it is the webmail and
submission password):

```
api$ curl -s -X POST -H "Authorization: Bearer API_TOKEN" -H "Content-Type: application/json" \
       http://127.0.0.1:8025/api/mailboxes \
       -d '{"tenantSlug":"imagiqa","address":"arun@anjal.co.in","password":"CHOOSE-A-STRONG-PASSWORD","displayName":"Arun Shiva B"}' | jq .
```

**Check:** `https://mail.anjal.co.in/` login works with that address and
password; the sidebar shows INBOX, Sent, Drafts, Junk, Trash and `0 KB of 2 GB`.

---

## 9. DNS - part two (allow sending)

Now relax the lockdown you set up months ago. At GoDaddy:

| Type | Name | Old value | New value |
|---|---|---|---|
| TXT | `@` | `v=spf1 -all` | `v=spf1 mx ip4:203.0.113.10 -all` (add `ip6:2001:db8::10` if you published AAAA) |
| TXT | `default._domainkey` | (none) | the `v=DKIM1; k=rsa; p=...` value from section 8 |
| TXT | `_dmarc` | `...rua=mailto:arunshiva_b@yahoo.com;` | same record with `rua=mailto:arun@anjal.co.in` |

Keep `p=reject` and strict alignment in DMARC - Anjal signs with the
same domain it sends from, so alignment passes.

`anjalmail.com` stays locked (`v=spf1 -all`, `p=reject`) until it is
used for hosted customers.

**Check** (after propagation):

```
pc> nslookup -type=TXT anjal.co.in 8.8.8.8
pc> nslookup -type=TXT default._domainkey.anjal.co.in 8.8.8.8
pc> nslookup -type=TXT _dmarc.anjal.co.in 8.8.8.8
```

and https://mxtoolbox.com/SuperTool.aspx → "dkim:anjal.co.in:default" reports a valid record.

---

## 10. First mail - inbound, outbound, and the deliverability check

**Inbound:** from Gmail or Yahoo, send a message to `arun@anjal.co.in`.
The first attempt is **greylisted** - the sender's server gets `451` and
retries within a few minutes; that is expected once per sender. Watch:

```
vm$ sudo journalctl -u anjal-server -f
```

Expect `Greylisted` on the first RCPT, then `Spam: score N ...` and
`Delivered arun@anjal.co.in -> imagiqa/arun@anjal.co.in/new/...`. Open
the message in the webmail; the message page shows the spam score and
reasons (a Gmail message should score 0-1).

**Outbound:** from the webmail, compose to your Gmail address and send.
Then in Gmail open the message → three dots → **Show original**. It must
say `SPF: PASS`, `DKIM: PASS with domain anjal.co.in`, `DMARC: PASS`.
If any is FAIL, section 9 is wrong; do not send more until fixed.

**Deliverability score:** send a message from the webmail to the address
shown at https://www.mail-tester.com/ and read the report; aim for 9+/10.
The usual point losses are a missing PTR (section 5) or a missing
`List-Unsubscribe` header, which does not apply to personal mail.

**Check:** both directions work, Gmail shows three PASSes, mail-tester ≥ 9.

---

## 11. Backups

Put the B2 credentials and two fresh crypt passwords into
`/etc/anjal/rclone.conf` (template already installed):

```
vm$ rclone obscure "$(openssl rand -base64 32)"     # run twice; paste as password and password2
vm$ sudo nano /etc/anjal/rclone.conf
vm$ sudo -u anjal rclone --config /etc/anjal/rclone.conf lsd b2crypt:   # empty listing, no error
```

**Copy `/etc/anjal/rclone.conf` to a password manager now.** The crypt
passwords are the only way to read the backup; losing them makes the
backup worthless.

First run by hand, then let the timer take over (nightly 21:00 UTC):

```
vm$ sudo systemctl start anjal-backup
vm$ sudo journalctl -u anjal-backup --no-pager | tail -20
vm$ sudo -u anjal rclone --config /etc/anjal/rclone.conf lsf b2crypt:db/
vm$ sudo -u anjal rclone --config /etc/anjal/rclone.conf size b2crypt:mail/
vm$ systemctl list-timers anjal-backup.timer
```

**Check:** the log ends with `verify ok` and `done`; `db/` lists one
`anjal-*.sql.gz`; the timer shows a next run.

---

## 12. Restore drill

A backup nobody has restored is a hope, not a backup. Do this once now,
on a **second, temporary VM** (smallest plan, one hour):

1. Sections 1-4 on the new VM (same env files; copy `rclone.conf`).
2. Do **not** start the services. Run:
   ```
   vm$ sudo /opt/anjal/bin/restore.sh
   ```
3. Start the services, log in to the webmail on the new VM's IP (use
   `https://<new-ip>/` and accept the name mismatch, or temporarily
   point `mail` at it), and confirm the messages from section 10 are
   there with the same folders and flags.
4. Destroy the temporary VM.

Repeat the drill after any change to `backup.sh`, and at least twice a year.

---

## 13. Operating

| Task | Command |
|---|---|
| Logs | `journalctl -u anjal-server -f`, `journalctl -u anjal-webmail -f` |
| Health | `curl -s http://127.0.0.1:8025/healthz \| jq .` |
| Metrics | `curl -s -H "Authorization: Bearer API_TOKEN" http://127.0.0.1:8025/metrics` |
| Certificate status | `curl -s -H "Authorization: Bearer API_TOKEN" http://127.0.0.1:8025/api/acme \| jq .` |
| Force renewal | `curl -s -X POST -H "Authorization: Bearer API_TOKEN" http://127.0.0.1:8025/api/acme/renew` |
| Upgrade | `publish.ps1` → `scp` → `tar -xzf` → `sudo ./bin/install.sh` → `sudo systemctl restart anjal-server anjal-webmail` |
| Roll back | `sudo mv /opt/anjal/server.old /opt/anjal/server` (same for webmail) → restart |
| Add a mailbox | `POST /api/mailboxes` (section 8) |
| Block a sender for a tenant | `POST /api/tenants/imagiqa/sender-rules -d '{"pattern":"@spammer.example","action":"block"}'` |
| Spam threshold | `POST /api/tenants -d '{"slug":"imagiqa","displayName":"imagiQa","spamThreshold":5}'` |

Things to watch in the first weeks:

- `/healthz` `tls` component and the 60-day renewal date.
- `/metrics` `anjal_greylist_deferred_total` vs `anjal_smtp_messages_accepted_total`
  - greylisting should defer far fewer than it lets through after the
  first day, because passed triplets are remembered.
- The Junk folder. Every message there has a score and reasons; if
  legitimate mail lands there, raise the tenant threshold or add an
  allow rule. If spam lands in INBOX, note the reasons it *did* trigger
  and what it should have - that is the data for the next anti-spam
  release.
- Disk: `df -h /var/mail` monthly. The 2 GB default quota per mailbox
  bounds it.

## 14. When something is wrong

| Symptom | Look at | Likely cause |
|---|---|---|
| Webmail unreachable | Security Group rules, `firewall-cmd --list-ports`, `journalctl -u anjal-webmail` | port 443 closed in **either** firewall, or service failed to bind (must run with `CAP_NET_BIND_SERVICE` - the unit sets it) |
| `/healthz` `tls` degraded, "no certificate" | `journalctl -u anjal-webmail \| grep ACME` | challenge failed: DNS, port 80, or PTR; fix and restart webmail |
| Inbound mail never arrives | `journalctl -u anjal-server \| grep -i "rcpt\|relaying"` | MX not pointing here, or domain not registered to a tenant (`GET /api/tenant-domains`) |
| Inbound lands in Junk | message page → spam reasons | see section 13; `SPF_NONE`/`DKIM_NONE` from a big provider means your DNS resolver on the VM is failing - check `resolvectl status` |
| Outbound stuck | `/metrics` `anjal_outbound_pending`, then `nc -vz gmail-smtp-in.l.google.com 25` from the VM | outbound 25 blocked in the Security Group or by E2E (sections 1a/1b), or recipient greylisting you (normal, retries) |
| Gmail shows `DKIM: FAIL` | section 9 TXT record | public key mismatch, selector typo, or TXT split into wrong chunks by the DNS panel |
| `452 4.2.2 Mailbox full` in logs | webmail sidebar usage | quota reached; delete mail or raise `quotaBytes` via `POST /api/mailboxes` |
| Backup fails | `journalctl -u anjal-backup` | rclone config unreadable by `anjal` (mode 640, group anjal), wrong B2 key, or `pg_dump` cannot connect (`ANJAL_POSTGRES` in `server.env`) |
