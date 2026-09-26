# Anjal deployment runbook - Ubuntu 24.04 on E2E Networks

This is the complete, in-order procedure for taking a fresh VM to a
running Anjal mail server for `anjal.co.in`: OS hardening, PostgreSQL,
the two services, DNS, TLS via the built-in ACME client, the first
tenant and mailbox, first inbound and outbound mail, backups, and a
restore drill. Every step has a check; do not move on until the check
passes. Expect two to three hours end to end, most of it waiting for DNS.

Sections 0 to 5 were rewritten for v1.0.0-rc.1 from the first real
provisioning (E2E Chennai, 25-26 September 2026), and sections 1 to 6 again
for v1.0.0-rc.2 after the first real start found DEF-048 and DEF-049.
Where this runbook earlier described what the provider was expected to do,
it now records what the provider was measured to do.

Conventions: commands prefixed `vm$` run on the VM over SSH as your own
named admin account (`arun` in the examples; section 1b creates it) and
use `sudo` where needed; `pc>` runs on the laptop in PowerShell; `api$`
is a `curl` against the admin API on the VM.

Placeholders used throughout - substitute your real values everywhere:

| Placeholder | Meaning |
|---|---|
| `203.0.113.10` | the VM's public IPv4 |
| `2001:db8::10` | the VM's public IPv6, if E2E assigns one (the first VM had none) |
| `mail.anjal.co.in` | the mail host name (SMTP banner, webmail, certificate) |
| `anjal.co.in` | the mail domain (addresses are `user@anjal.co.in`) |
| `$TOKEN` | the admin API token, read once per session with `TOKEN=$(sudo grep '^ANJAL_API_TOKEN=' /etc/anjal/server.env \| cut -d= -f2-)` and cleared with `unset TOKEN`. Never type or paste it: that puts it on screen and in shell history |
| `1.0.0-rc.2` | the release being deployed; always a tag, never an untagged build |

---

## 0. Before ordering the VM

1. **Decide the size.** For one to three tenants: 2 vCPU, 4-6 GB RAM,
   75-80 GB disk is comfortable; mail is I/O-light. Pick a plan with a
   **static (reserved) public IPv4** - reputation is tied to the IP, and a
   changing IP means re-doing SPF and PTR. Enable **encryption at rest**
   when creating the node, without a passphrase, so it boots unattended.
   Decline E2E's own backup (CDP): Anjal backs up to Backblaze B2 itself
   (section 11).
2. **Port 25 outbound - test it, do not ask about it.** Most cloud
   providers block it by default to stop spam, and no document answers the
   question for your account. Take the node, attach a Security Group whose
   outbound rule is ALL (section 1a), and run
   `nc -vz gmail-smtp-in.l.google.com 25`. On E2E Chennai in September 2026
   this connected first time, and `nc gmail-smtp-in.l.google.com 25`
   returned Gmail's `220` greeting - no ticket was needed. If it fails,
   raise a ticket quoting the output. If it cannot be opened at all,
   outbound mail must go through a relay (`ANJAL_OUTBOUND_MODE=relay`) - an
   owner's decision, since a relay reintroduces the dependency this
   project exists to remove.
3. **PTR (reverse DNS).** On E2E this is self-service: MyAccount →
   Network → DNS → **Add Reverse DNS**. The node's IP already has a PTR
   row pointing at E2E's own name; you **Edit** it in section 5, after the
   A record exists. No ticket.
4. **IP reputation.** Once you have the IP, check it is not on common
   blocklists before building anything on it:
   https://mxtoolbox.com/blacklists.aspx (enter the IP). If it is listed
   on Spamhaus or Barracuda, ask E2E for a different IP now - delisting a
   burned IP is slower than swapping it.
5. **Backblaze B2.** Create a bucket named `anjal-backup` (private,
   default encryption off - rclone encrypts client-side) and an
   application key restricted to that bucket with read/write/delete.
   Keep the key ID and application key for section 11. This can wait
   until section 11; nothing before it needs B2.

---

## 1. Base OS

E2E's Ubuntu 24.04 image logs in as `root` with the SSH key you gave at
creation; it has no `ubuntu` user. The first session is therefore as root,
and ends with root's SSH login switched off (section 1b).

```
vm$ apt update && apt full-upgrade -y
```

During the upgrade, `openssh-server` asks what to do about a modified
`/etc/ssh/sshd_config`. Choose **keep the local version currently
installed** - E2E's version is the one that already refuses passwords.
If `needrestart` then reports a pending kernel upgrade, accept its
defaults and reboot (below).

```
vm$ ls /var/run/reboot-required && reboot    # if a new kernel was installed
vm$ uname -r                                 # after reconnecting: the new kernel
vm$ systemctl is-system-running              # "running"
vm$ timedatectl                              # "System clock synchronized: yes"
```

**Time zone: leave it as provisioned** (Asia/Kolkata on E2E India). Anjal
stores and computes every time in UTC internally - all timestamps are
`TIMESTAMPTZ` and the code uses `UtcNow` - so the zone only changes how
logs read, and India has no daylight saving to make local logs ambiguous.
The backup timer names UTC explicitly, so it is unaffected.

**Host name: leave it as provisioned.** Anjal does not use the machine's
host name: the SMTP banner, EHLO and Message-IDs all come from
`ANJAL_HOSTNAME` in `server.env`. E2E images are managed by OpenNebula's
one-context (`/etc/one-context.d/net-15-hostname`), which sets the host
name at every boot, so a manual change would not survive anyway.

Tools used later (PostgreSQL is section 2):

```
vm$ apt install -y unattended-upgrades fail2ban rclone curl jq netcat-openbsd
vm$ cat /etc/apt/apt.conf.d/20auto-upgrades     # both lines "1": security updates are automatic
vm$ fail2ban-client status sshd                 # the jail is running
```

fail2ban on Ubuntu 24.04 reads the systemd journal and bans through
nftables (`/etc/fail2ban/jail.d/defaults-debian.conf`). Expect it to report
failed logins within minutes of the node going live: bots try every
address on port 22. With key-only login they cannot succeed.

Its default `normal` mode does not count the commonest bot pattern against a
key-only server - `Connection closed by authenticating user root ...
[preauth]`. Measured with fail2ban 1.0.2 against this server's own journal:
`normal` matched 0 of 4 such lines, `aggressive` matched all 4 and still
ignored the admin's accepted login. Count them, and ban for an hour:

```
vm$ sudo tee /etc/fail2ban/jail.d/anjal-sshd.local >/dev/null <<'CONF'
[sshd]
mode = aggressive
bantime = 1h
findtime = 10m
maxretry = 5
CONF
vm$ sudo systemctl restart fail2ban
vm$ sudo fail2ban-client get sshd bantime       # 3600
```

### 1a. Firewall: the Security Group, and nothing on the VM

E2E's portal advises using **Security Groups alone** and stopping host
firewalls on the VM. This runbook follows that advice: there is **no
firewalld and no ufw** on the VM. One firewall means one place to change a
port and no way for two layers to disagree and lock you out.

What makes a host firewall unnecessary here is that nothing internal
listens on a public address: the admin API is bound to `127.0.0.1:8025`
and PostgreSQL to `127.0.0.1:5432`. Section 1c measures this rather than
assuming it.

**Security Group** (portal → Network → Security Group → Create), attached
to the node **instead of** the default group:

| Direction | Protocol | Ports | Source / destination | Why |
| --- | --- | --- | --- | --- |
| Inbound | Custom TCP | 22 | Any (see below) | SSH, for administration only - mail never uses it |
| Inbound | Custom TCP | 25 | Any | every mail server on the internet delivers here |
| Inbound | Custom TCP | 80 | Any | ACME HTTP-01 challenge, and the redirect to HTTPS |
| Inbound | Custom TCP | 443 | Any | webmail |
| Inbound | Custom TCP | 587 | Any | your own clients, authenticated (STARTTLS) |
| Inbound | Custom TCP | 465 | Any | your own clients, authenticated (implicit TLS, RFC 8314) |
| Outbound | ALL | - | Any | see the warning below |

**Leave outbound permissive.** Anjal must reach port 25 on other mail
servers, 53 for DNS, and 443 for Let's Encrypt. A tightened outbound rule
that forgets DNS produces a server that looks healthy and silently fails
every delivery and every certificate renewal. If you must restrict it,
allow at least TCP 25, 53, 80, 443 and UDP 53.

**Port 22 policy.** Through deployment and phase B testing, port 22 stays
open to Any: the admin's own IP changes, so restricting it to one address
means lock-outs. Key-only login, root login off (section 1b), fail2ban and
automatic security updates make that safe against the bots. The residual
risk is a future pre-authentication flaw in OpenSSH itself (as in 2024).
**Before real mail flows**, SSH moves behind a WireGuard tunnel and port 22
is removed from the Security Group entirely; that change gets its own
section when it is made.

From the laptop, after the Security Group is attached:

```
pc> Test-NetConnection -ComputerName 203.0.113.10 -Port 22
pc> Test-NetConnection -ComputerName 203.0.113.10 -Port 443     # fails until section 6 - nothing listens yet
```

### 1b. Your own admin account, and root's SSH login switched off

Admin accounts are **one per person, never shared**. `sudo` gives control
of the whole machine, so an account per application (`anjaladmin`,
`sangamadmin`) would look separated without being so; the separation
between applications is their service users (`anjal`, and so on).

Still as root, check which keys root accepts, then create the account:

```
vm$ awk '{print NR": "$1" ... "$NF}' /root/.ssh/authorized_keys   # expect only your own key
vm$ adduser --gecos "Your Name" arun            # the password asked for is your sudo password
vm$ usermod -aG sudo arun
vm$ install -d -m 700 -o arun -g arun /home/arun/.ssh
vm$ install -m 600 -o arun -g arun /root/.ssh/authorized_keys /home/arun/.ssh/authorized_keys
vm$ id arun                                     # includes 27(sudo)
```

Write the sudo password on the custody forms (ANJAL-SEC-03). **Keep the
root session open** and prove the new account from a second window:

```
pc> ssh -i $env:USERPROFILE\.ssh\anjal_e2e arun@203.0.113.10
vm$ sudo whoami                                 # asks for the sudo password, prints root
```

Only then, as `arun`, write the SSH policy. E2E's image carries a
Red Hat file, `/etc/ssh/sshd_config.d/50-redhat.conf`, that turns on
GSSAPI (Kerberos) and X11 forwarding. Leave that file alone - an image
update may restore it - and override it: `sshd` reads `sshd_config.d`
before the main file (the `Include` is on line 15), in name order, and
the first value read for a setting wins, so `01-` beats both.

```
vm$ sudo tee /etc/ssh/sshd_config.d/01-anjal-hardening.conf >/dev/null <<'CONF'
# Anjal server SSH policy. Read before 50-redhat.conf and the main
# sshd_config; for each setting the first value read wins.
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
GSSAPIAuthentication no
X11Forwarding no
# E2E's sshd_config names Red Hat's sftp-server path, which does not exist
# on Ubuntu; without this line scp and sftp log in and are then closed.
Subsystem sftp /usr/lib/openssh/sftp-server
CONF
vm$ sudo chmod 644 /etc/ssh/sshd_config.d/01-anjal-hardening.conf
vm$ sudo sshd -t && echo "sshd config OK"
vm$ sudo sshd -T | grep -Ei '^(permitrootlogin|passwordauthentication|kbdinteractiveauthentication|pubkeyauthentication|gssapiauthentication|x11forwarding|usepam|subsystem)'
```

The last command must print `permitrootlogin no`, `passwordauthentication
no`, `kbdinteractiveauthentication no`, `pubkeyauthentication yes`,
`gssapiauthentication no`, `x11forwarding no`, `usepam yes` and
`subsystem sftp /usr/lib/openssh/sftp-server`. The `Subsystem` line is
needed because line 123 of E2E's main `sshd_config` names
`/usr/libexec/openssh/sftp-server`, Red Hat's path, which does not exist on
Ubuntu. OpenSSH 9.6 takes the first definition it reads (measured), so the
drop-in wins without editing E2E's file. If `sshd -t`
complains `Missing privilege separation directory: /run/sshd`, run
`sudo mkdir -p /run/sshd` and repeat - on 24.04 SSH is socket-activated and
that directory exists only while the service runs.

```
vm$ sudo systemctl restart ssh
```

**Check**, from two new windows while the old ones stay open:
`ssh ... root@203.0.113.10` must answer `Permission denied (publickey)`,
and `ssh ... arun@203.0.113.10` must log in. Only then close the root
session. From here on every `vm$` command runs as `arun`. If SSH ever
breaks, E2E's web console still logs in as root with the console password
(also on the custody forms); the console does not use SSH.

### 1c. What else is on the image, and what faces the internet

E2E's image ships agents of its own. Measure them:

```
vm$ systemctl list-units --type=service --all --no-pager | grep -Ei 'cdp|sbm|zabbix|one-context'
vm$ ip -6 addr show scope global                # empty: no public IPv6, nothing to guard there
vm$ sudo ss -tlnp
```

- **R1Soft CDP backup agent** (`sbm-agent`, `cdp-agent`, port 1167) - the
  E2E backup you declined, installed and running anyway, with read access
  to the whole disk. Switch it off:
  `sudo systemctl disable --now sbm-agent.service cdp-agent.service`.
  Its apt source (`repo.r1soft.com`) can stay; updates do not re-enable a
  disabled service.
- **Zabbix agent** (`zabbix-agent`, port 10050) - feeds the graphs and
  alerts in the E2E portal and answers only E2E's monitoring server
  (`Server=` in `/etc/zabbix/zabbix_agentd.conf`, a 10.x address). Keep it.

**Check:** after section 6, `sudo ss -tlnp` shows exactly: 22, 25, 80,
443, 465 and 587 on all addresses; 5432 and 8025 on `127.0.0.1` only; 10050
(Zabbix, blocked by the Security Group). Anything else listening publicly
is a finding.

---

## 2. PostgreSQL

Install **PostgreSQL 18** from the PostgreSQL project's own repository,
not Ubuntu's archive. Ubuntu freezes one major version per LTS release and
24.04's is 16; Anjal's manual QA ran on 18, and upstream supports 18 about
two years longer than 16. A major-version change is free now, on an empty
database, and a migration later.

```
vm$ sudo apt install -y postgresql-common
vm$ sudo /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh     # press Enter when asked
vm$ apt-cache policy postgresql-18                                  # candidate from apt.postgresql.org
vm$ sudo apt install -y postgresql-18
```

Install `postgresql-18` **by name**, never the bare `postgresql` package:
from this repository that would follow the newest major version, and a
PostgreSQL 19 would arrive without anyone deciding it should.

If Ubuntu's 16 was installed first, remove it before installing 18 (18
then takes port 5432): `sudo pg_dropcluster --stop 16 main` and
`sudo apt purge -y postgresql postgresql-16 postgresql-client-16`. Do not
run `apt autoremove` before `postgresql-common` has been installed by name
- it removes the package that holds the repository script.

Automatic security updates cover only Ubuntu's own sources by default.
Add this repository, or 18 is never patched unattended:

```
vm$ echo 'Unattended-Upgrade::Origins-Pattern { "site=apt.postgresql.org"; };' | sudo tee /etc/apt/apt.conf.d/52unattended-upgrades-pgdg
vm$ sudo unattended-upgrade --dry-run --debug 2>&1 | grep -i 'allowed origins'     # ends with site=apt.postgresql.org
```

**Check:**

```
vm$ sudo pg_lsclusters                          # 18  main  5432  online
vm$ cd /tmp && sudo -u postgres psql -Atc "show listen_addresses;" -c "show data_checksums;"; cd ~
```

`localhost` and `on`. PostgreSQL listens on 127.0.0.1 only by default;
leave it that way. Data checksums are on by default in 18: corruption on
disk is reported instead of silently returned.

Create the role and the database. The password is the **first of the
secrets**: generate it on the VM, write it on the custody forms as it
appears, and never type it on a command line (command lines are kept in
shell history).

```
vm$ openssl rand -hex 24                        # 48 characters, 0-9 and a-f only: no O/0 or l/1 to misread
vm$ cd /tmp && sudo -u postgres createuser --login --pwprompt anjal; cd ~     # paste it twice
vm$ cd /tmp && sudo -u postgres createdb --owner anjal anjal; cd ~
vm$ psql "host=127.0.0.1 dbname=anjal user=anjal" -c 'select current_user;'   # asks for it; prints anjal
```

The schema is applied in section 3, from the release itself.

---

## 3. Build and upload the release

On the laptop, from the repo root at the tagged release:

```
pc> git checkout v1.0.0-rc.2
pc> .\deploy\publish.ps1 -Version 1.0.0-rc.2
pc> (Get-FileHash .\artifacts\anjal-1.0.0-rc.2.tar.gz -Algorithm SHA256).Hash.Substring(0,16)
pc> scp -i $env:USERPROFILE\.ssh\anjal_e2e .\artifacts\anjal-1.0.0-rc.2.tar.gz arun@203.0.113.10:~/
```

`publish.ps1` produces self-contained linux-x64 builds of both
processes (no .NET runtime to install on the VM), plus `bin/` with the
scripts, units, env templates and `schema.sql`. Note the 16-character
hash: the VM must show the same value.

If `scp` logs in and then says only `Connection closed`, the VM's SSH
server cannot start its SFTP helper - section 1b's `Subsystem` line is
missing (current Windows `scp` transfers over SFTP).

On the VM - always through `bash`, because a release built on Windows
arrives with no executable bits, `install.sh` included:

```
vm$ sha256sum anjal-1.0.0-rc.2.tar.gz | cut -c1-16      # the laptop's value, in lower case
vm$ tar -xzf anjal-1.0.0-rc.2.tar.gz
vm$ sudo bash ./anjal-1.0.0-rc.2/bin/install.sh ~/anjal-1.0.0-rc.2
```

`install.sh` creates the `anjal` system user, the directory layout below,
installs the binaries under `/opt/anjal` and marks them executable, writes
config **templates** to `/etc/anjal` (it never overwrites an existing
config) and installs and enables the systemd units without starting them.

| Path | Owner / mode | Purpose |
|---|---|---|
| `/opt/anjal/server`, `/opt/anjal/webmail` | root 755 | binaries (previous release kept as `*.old`) |
| `/opt/anjal/bin` | root 755 | `backup.sh`, `restore.sh` |
| `/etc/anjal/server.env`, `webmail.env`, `rclone.conf` | root:anjal 640 | configuration and secrets |
| `/var/mail/anjal` | anjal 700 | Maildirs, one per mailbox |
| `/var/lib/anjal/acme` | anjal 700 | ACME account key, certificate, key, status |
| `/var/lib/anjal/backup` | anjal 700 | scratch for `pg_dump` |
| `/var/lib/anjal/webmail-keys` | anjal 700 | keys signing webmail sessions and form tokens |

`/etc/anjal` is private to root and the `anjal` group: as your own account,
read it with `sudo`.

**Check** - the Linux binary runs on this machine. It must run as `anjal`,
the only user that can write the ACME folder:

```
vm$ sudo -u anjal /opt/anjal/server/Anjal.Server --acme-renew-now     # "Renewal requested; marker written ..."
vm$ sudo rm -f /var/lib/anjal/acme/renew.request
```

Apply the schema from the release (it asks for the database password; type
it from a paper copy - that proves the copy too):

```
vm$ psql "host=127.0.0.1 dbname=anjal user=anjal" -v ON_ERROR_STOP=1 -f ~/anjal-1.0.0-rc.2/bin/schema.sql
vm$ psql "host=127.0.0.1 dbname=anjal user=anjal" -c '\dt'
```

On a first run one `NOTICE: trigger "audit_events_no_change" ... does not
exist, skipping` is expected; on a re-run, `already exists, skipping`
notices are expected. The script is safe to apply again, which is how an
upgrade applies it. **Check:** `\dt` lists **20 tables**, all owned by
`anjal`, including `tenants`, `mailboxes`, `messages`, `sender_rules` and
`audit_events` (measured on PostgreSQL 18.6, with the file's CRLF line
endings exactly as shipped).

---

## 4. Configure

Three secrets go into the env files. Each is **generated on the VM** -
never on the laptop: Windows PowerShell 5.1 once produced an admin token of
forty identical characters from a .NET Core-only API - and kept on the
handwritten custody forms (ANJAL-SEC-03, three copies), not in a password
manager. The method below never makes you retype a secret into a file:
each is generated into a shell variable, shown once for the forms, written
into the file from the variable, and cleared from the screen. **Never paste
a secret into a chat or a ticket.** Run each box on its own.

**The database password** (created in section 2) - typed silently from
paper, proven against the database, then written into both files:

```
vm$ read -rs -p "Database password (from paper): " PGPW; echo
vm$ PGPASSWORD="$PGPW" psql "host=127.0.0.1 dbname=anjal user=anjal" -Atc 'select current_user;'   # must print anjal
vm$ sudo sed -i "s|Password=CHANGE-ME|Password=$PGPW|" /etc/anjal/server.env /etc/anjal/webmail.env
vm$ unset PGPW
```

**The admin API token** - 48 characters, 0-9 and a-f only, so a written
copy has no O/0 or l/1 to misread. The server refuses a token that is short,
lacks variety or looks like a placeholder. Rotating it is section 13a.

```
vm$ TOKEN=$(openssl rand -hex 24); echo "$TOKEN"
```

Write it on all three forms (row "Administration API token"), then:

```
vm$ sudo sed -i "s|^ANJAL_API_TOKEN=.*|ANJAL_API_TOKEN=$TOKEN|" /etc/anjal/server.env; unset TOKEN; clear
```

**The key-encryption key** - seals the DKIM private keys in the database,
and is the **only secret that cannot be reset**: a backup restored without
it has unreadable DKIM keys, recoverable only by generating new keys and
republishing DNS. It must be base64 of exactly 32 bytes: 44 characters,
case-sensitive, may contain `+` and `/`, ends with `=`.

```
vm$ KEK=$(openssl rand -base64 32); echo "$KEK"
```

Write it in the first row of all three forms, marking the look-alikes
(`0`/`O`, `1`/`l`/`I`), then:

```
vm$ sudo sed -i "s|^ANJAL_KEK=.*|ANJAL_KEK=$KEK|" /etc/anjal/server.env; unset KEK; clear
```

**The certificate rehearsal** - staging on in `webmail.env` only (section 7
turns it off again):

```
vm$ sudo sed -i 's|^# ANJAL_ACME_STAGING=true.*|ANJAL_ACME_STAGING=true|' /etc/anjal/webmail.env
```

**Prove every paper copy.** Type each secret from copies 1, 2 and 3 in
turn; nothing shows, nothing is stored in history, and only a fingerprint
is compared:

```
vm$ prove() { local want got c
      want=$(sudo grep "^$1=" /etc/anjal/server.env | cut -d= -f2- | tr -d '\n' | sha256sum | cut -c1-16)
      for c in 1 2 3; do
        read -rs -p "$1 from paper copy $c: " got; echo
        if [ "$(printf '%s' "$got" | sha256sum | cut -c1-16)" = "$want" ]; then echo "copy $c: MATCH"; else echo "copy $c: NO MATCH - check this copy"; fi
      done; }
vm$ prove ANJAL_KEK
vm$ prove ANJAL_API_TOKEN
```

For the database password, the proof is a login: run the `psql ... select
current_user;` line three times, typing from each copy.

**Check** (nothing below shows a secret):

```
vm$ sudo awk '/^ANJAL_(API_TOKEN|KEK)=/{i=index($0,"="); print substr($0,1,i-1), "length", length(substr($0,i+1))}' /etc/anjal/server.env   # 48 and 44
vm$ sudo grep '^ANJAL_KEK=' /etc/anjal/server.env | cut -d= -f2- | base64 -d | wc -c          # 32
vm$ sudo grep -c 'CHANGE-ME' /etc/anjal/server.env /etc/anjal/webmail.env                      # 0 and 0
vm$ sudo grep -E '^ANJAL_(ACME_STAGING|WEBMAIL_KEYS_DIR)' /etc/anjal/server.env /etc/anjal/webmail.env
vm$ for f in server webmail; do
      sudo bash -c "pw=\$(grep '^ANJAL_POSTGRES=' /etc/anjal/$f.env | sed -E 's/.*Password=([^;]*).*/\1/'); PGPASSWORD=\"\$pw\" psql 'host=127.0.0.1 dbname=anjal user=anjal' -Atc 'select current_user'" && echo "$f.env: database login OK"
    done
vm$ sudo -u anjal cat /etc/anjal/server.env >/dev/null && echo readable
vm$ sudo ls -l /etc/anjal                                                                       # root anjal, -rw-r----- on all three
```

Expect `ANJAL_ACME_STAGING=true` in `webmail.env` only, and
`ANJAL_WEBMAIL_KEYS_DIR=/var/lib/anjal/webmail-keys` in `webmail.env`. Both
files must agree on `ANJAL_POSTGRES`, `ANJAL_MAILDIR_ROOT`, `ANJAL_ACME_DIR`
and `ANJAL_ACME_DOMAINS`; every variable is explained in `README.md`.

---

## 5. DNS - part one (before first start)

At GoDaddy, for `anjal.co.in`:

| Type | Name | Value | TTL |
|---|---|---|---|
| A | `mail` | `203.0.113.10` | 600 |
| AAAA | `mail` | `2001:db8::10` (only if E2E gave you IPv6 **and** it has PTR) | 600 |
| MX | `@` | `10 mail.anjal.co.in` | 600 |

Leave SPF as `v=spf1 -all` and DMARC as it is for now (nothing sends yet).

At E2E, after the A record answers: MyAccount → Network → DNS → **Add
Reverse DNS**. The IP's existing PTR row points at E2E's own name; open its
**⋯** menu → **Edit**, set **Target** to `mail.anjal.co.in.` (with the
trailing dot - it marks the name as complete), leave TTL, and Submit. Edit
the row rather than adding another: two PTRs for one IP confuse receivers.
E2E warns that changes can take seven days; the first one answered from
E2E, Google and Cloudflare within minutes.

Wait for propagation, then check from the laptop:

```
pc> nslookup -type=A mail.anjal.co.in. 8.8.8.8
pc> nslookup -type=MX anjal.co.in. 8.8.8.8
pc> nslookup 203.0.113.10 8.8.8.8          # PTR must answer mail.anjal.co.in
```

Keep the **trailing dot** on names. Without it, Windows retries a name
that does not exist yet with the network's search suffix appended; on a
home router that hands out `domain.name`, that returns ten unrelated
`185.38.109.x` addresses for `mail.anjal.co.in.domain.name`, which looks
like a wrong A record but is only "not there yet".

**Check:** all three answer correctly from a public resolver. The PTR is
the one people forget; several large providers refuse mail from IPs
whose PTR does not match the HELO name.

---

## 6. First start

```
vm$ sudo systemctl start anjal-server
vm$ sudo journalctl -u anjal-server -n 40 --no-pager
```

Expected lines: `Inbound authentication: SPF+DKIM+DMARC enabled, DMARC
p=reject ENFORCED.`, `Anjal SMTP (MTA, port 25)`, `Using PostgreSQL store.`,
`STARTTLS waiting for the ACME certificate; it is picked up automatically
when issued, no restart needed.`, `Anjal SMTP (Submission, port 587)
listening`, `API listening on http://127.0.0.1:8025`, `Health at ...`.
`systemctl is-active anjal-server` must still say `active` ten seconds
later. The bracketed times in Anjal's own lines are UTC; the journal's own
column is the server's zone.

If the server stops at once with `NetworkInformationException (97):
Address family not supported by protocol`, the release is older than
v1.0.0-rc.2 (DEF-048); section 13d describes the rc.1 workaround.

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
api$ curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/acme | jq .
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
no rate-limit risk. Read the admin token into the shell once (see the
conventions table), note the current certificate, force a renewal and wait
for it:

```
vm$ start=$(date '+%Y-%m-%d %H:%M:%S')
vm$ TOKEN=$(sudo grep '^ANJAL_API_TOKEN=' /etc/anjal/server.env | cut -d= -f2-)
api$ curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/acme | jq '{issuedAt, notBefore, acmeDirectoryUrl}'
api$ curl -s -X POST -d '' -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/acme/renew     # 202 {"requested":true,...}
vm$ for i in $(seq 1 24); do sleep 5; sudo journalctl -u anjal-webmail --since "$start" --no-pager | grep -q "certificate issued" && break; done
vm$ sudo journalctl -u anjal-webmail --since "$start" --no-pager | grep ACME     # "renew-now request found" ... "certificate issued"
api$ curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/acme | jq '{issuedAt, notBefore, acmeDirectoryUrl}'
```

`issuedAt` must have changed. **Always send `-d ''` with a bodyless
POST**: without it curl sends no `Content-Length`, and the answer is `411
Length Required`. Up to v1.0.0-rc.2 the request was then carried out anyway
(DEF-053) - an operator who retried would request a certificate each time
and exhaust Let's Encrypt's limit of five duplicates a week. Since rc.3 a
411 changes nothing.

Then confirm both processes picked up the new certificate without a
restart. The mail server checks for a new certificate at most every 30
seconds, and only when a connection asks for one, so wait first:

```
vm$ sleep 31
vm$ openssl s_client -connect 127.0.0.1:587 -starttls smtp -servername mail.anjal.co.in </dev/null 2>/dev/null | openssl x509 -noout -startdate -issuer
vm$ openssl s_client -connect 127.0.0.1:443 -servername mail.anjal.co.in </dev/null 2>/dev/null | openssl x509 -noout -startdate -issuer
vm$ sudo journalctl -u anjal-server --since "$start" --no-pager | grep "TLS:"      # "certificate reloaded"
```

Both show the same new `notBefore`. `/healthz` reads the certificate file,
so its `tls: ok` alone does not prove the SMTP listeners have it - these
connections do.

**Now switch to production.** The staging account and certificate are not
valid in production, so the ACME folder is emptied first. Let root expand
the path: `/var/lib/anjal` is private to the `anjal` user, so in `sudo rm
-rf /var/lib/anjal/acme/*` your own shell cannot expand the `*`, and the
command silently removes nothing (DEF-052).

```
vm$ start=$(date '+%Y-%m-%d %H:%M:%S')
vm$ sudo systemctl stop anjal-webmail anjal-server
vm$ sudo find /var/lib/anjal/acme -mindepth 1 -delete
vm$ sudo ls -la /var/lib/anjal/acme                                    # only . and ..
vm$ sudo sed -i 's/^ANJAL_ACME_STAGING=true/# ANJAL_ACME_STAGING=true/' /etc/anjal/webmail.env
vm$ sudo systemctl start anjal-server anjal-webmail
vm$ for i in $(seq 1 24); do sleep 5; sudo journalctl -u anjal-webmail --since "$start" --no-pager | grep -q "certificate issued" && break; done
vm$ sudo journalctl -u anjal-webmail --since "$start" --no-pager | grep ACME     # acme-v02, no "staging"
vm$ echo | openssl s_client -connect 127.0.0.1:443 -servername mail.anjal.co.in 2>/dev/null | grep -E "^issuer=|Verify return code"
```

The issuer is Let's Encrypt without "(STAGING)" and the verify code is
`0 (ok)`. Measured on the first server: account, order, validation and
issue in eight seconds.

Force **one** more renewal exactly as in the rehearsal to prove production
renewal works, then leave it alone. Let's Encrypt allows 5 duplicate
certificates per week - do not loop this. The first server used two:
the first production certificate and this one.

**Check:** the browser shows a valid padlock on `https://mail.anjal.co.in/`;
`/api/acme` shows `acmeDirectoryUrl` without `staging`;
https://www.ssllabs.com/ssltest/analyze.html?d=mail.anjal.co.in grades A.
Then `unset TOKEN`.

Renewal is now automatic: the service checks hourly and renews at 30
days remaining. Mark the calendar for **60 days from today** and confirm
on that day that `issuedAt` has moved and `/healthz` `tls` is `ok`.
Until that has happened once, keep the tenant count at three or fewer.

---

## 8. First tenant, domain, DKIM and mailbox

Read the admin token into the shell (see the conventions table) and define
a short helper for the API calls:

```
vm$ TOKEN=$(sudo grep '^ANJAL_API_TOKEN=' /etc/anjal/server.env | cut -d= -f2-)
vm$ api() { curl -s -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" "$@"; }
api$ api -X POST http://127.0.0.1:8025/api/tenants -d '{"slug":"imagiqa","displayName":"imagiQa"}' | jq .
api$ api -X POST http://127.0.0.1:8025/api/tenant-domains -d '{"tenantSlug":"imagiqa","domain":"anjal.co.in"}' | jq .
```

Each answers with the record it created; the domain shows `verified: true`
(set on creation; nothing else checks it).

**DKIM.** Generate a 2048-bit key on the VM in a folder only you can open,
upload it straight from the file, keep only the public half for DNS, and
destroy the private file. The server stores the key sealed under
`ANJAL_KEK`, so no other copy is needed. Your account's default
permissions make new files readable by every account on the server
(`-rw-rw-r--`, measured), which is why the key is never written to plain
`/tmp` (DEF-054).

```
vm$ D=$(mktemp -d); ls -ld "$D"                                          # drwx------
vm$ (umask 077; openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$D/dkim.pem")
vm$ ls -l "$D/dkim.pem"                                                  # -rw-------
api$ jq -n --rawfile pem "$D/dkim.pem" '{domain:"anjal.co.in",selector:"default",privateKeyPem:$pem}' | api -X POST http://127.0.0.1:8025/api/dkim-keys -d @- | jq .
vm$ echo "v=DKIM1; k=rsa; p=$(openssl pkey -in "$D/dkim.pem" -pubout -outform DER | base64 -w0)" > ~/dkim-dns-record.txt
vm$ shred -u "$D/dkim.pem"; rmdir "$D"
```

The API answers with `domain` and `selector` and never echoes the key.
`~/dkim-dns-record.txt` (about 410 characters) is the **public** TXT value
for section 9. **Check** that the key is sealed, not stored in plain text
(asks for the database password; prints nothing secret):

```
vm$ psql "host=127.0.0.1 dbname=anjal user=anjal" -Atc "select selector, left(private_key_pem,7), length(private_key_pem), private_key_pem like '%BEGIN%' from dkim_keys"
```

Expect `default|enc:v1:|...|f`: sealed under the KEK, and no `BEGIN` in it.

**The mailbox.** The password is the webmail and submission password. It
is typed twice, silently, and handed to the API through the environment -
never on a command line, which would put it on screen and in shell history
(DEF-055). The rule: 8+ characters with upper case, lower case, a number
and a symbol, or a phrase of 16+; not predictable; not containing the
person's name or address. A refusal answers 400 and creates nothing.

```
vm$ read -rs -p "Password for arun@anjal.co.in: " P1; echo; read -rs -p "Again: " P2; echo
api$ if [ "$P1" = "$P2" ]; then P1="$P1" jq -n '{tenantSlug:"imagiqa",address:"arun@anjal.co.in",password:env.P1,displayName:"Arun Shiva B"}' | api -X POST http://127.0.0.1:8025/api/mailboxes -d @- | jq .; else echo "The two entries differ - nothing was sent"; fi
vm$ unset P1 P2 TOKEN
```

**Check:** `https://mail.anjal.co.in/` shows the sign-in page with no
certificate warning; signing in shows INBOX, Drafts, Junk, Sent and Trash
and `0 B of 2 GB`.

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

Send the DMARC reports to an address **in the same domain**. A report
address in another domain (such as a personal Yahoo address) is honoured
only if that domain publishes a record authorising it (RFC 7489 section
7.1); Yahoo does not, so the reports sent there during the lockdown were
most likely never delivered.

`anjalmail.com` stays locked (`v=spf1 -all`, `p=reject`) until it is
used for hosted customers.

**Check** against GoDaddy's own name server (no caching delay). The last
line proves the published DKIM value is byte for byte the key the server
holds - a 2048-bit key is longer than one DNS string, so GoDaddy splits it,
and the check joins the pieces before comparing:

```
vm$ command -v dig >/dev/null || sudo apt install -y bind9-dnsutils
vm$ dig +short TXT anjal.co.in @ns59.domaincontrol.com
vm$ dig +short TXT _dmarc.anjal.co.in @ns59.domaincontrol.com
vm$ diff <(dig +short TXT default._domainkey.anjal.co.in @ns59.domaincontrol.com | tr -d '" \n') <(tr -d ' \n' < ~/dkim-dns-record.txt) && echo "DKIM record MATCHES the server's key"
```

Use the name servers your domain actually has (`nslookup -type=NS anjal.co.in. 8.8.8.8`).

---

## 10. First mail - inbound, outbound, and the deliverability check

**Inbound:** from Gmail or Yahoo, send a message to `arun@anjal.co.in`.
Large providers pass SPF for their own domain, so since rc.5 they are **not
greylisted** and the message arrives within seconds. A sender that does not
pass SPF is greylisted once: its server gets `451`, retries on its own
schedule (measured 25-35 minutes for Gmail and Outlook before rc.5), and is
then remembered for 35 days, across restarts. Watch:

```
vm$ sudo journalctl -u anjal-server -f
```

Expect one `Greylist:` line - `not delayed - ... passes SPF for ...`, or
`deferred ... (451, retry in ...)` followed later by `passed after N min` -
then `Spam: score N ...` and
`Delivered arun@anjal.co.in -> imagiqa/arun@anjal.co.in/new/...`. Before
rc.5 no greylisting decision was logged, so the `Greylisted` line this
section used to promise never appeared (DEF-059). Open the message in the
webmail; the message page shows the spam score and reasons (a Gmail
message should score 0-1).

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

Backups are encrypted on the server by rclone's crypt layer before they
leave it, and go to a private cloud bucket. This section was executed on
26 September 2026 and is written from that execution.

> **Data residency (owner's decision, 26 September 2026).** Patient data
> must stay in India. Backblaze B2 has no Indian region, so it is used
> **only while Anjal holds no patient data** - test mail and the operator's
> own mail. **Before HIS goes live, or before any mail naming a patient
> reaches Anjal, whichever is first**, backups move to Indian storage (E2E
> Networks' EOS object storage is the planned choice) and the Backblaze
> bucket is deleted. See section 12a.

### 11.1 The Backblaze account

Sign up for B2 Cloud Storage with an email address that does **not**
depend on Anjal (a Gmail or Yahoo address, never one served by this
server): that email is how the backup account is recovered, and it must
work when the server does not. The storage region is chosen at sign-up
and is fixed for the account; none is in India. Turn on two-factor
authentication at once. The account email, its password and any
two-factor recovery codes go on the custody forms (ANJAL-SEC-03).

### 11.2 The bucket

Buckets → Create a Bucket:

| Setting | Value | Why |
| --- | --- | --- |
| Name | `anjal-backup` (names are global; add a suffix if taken, and use it in `rclone.conf`) | |
| Files in bucket | Private | Never public |
| Default encryption | Enable (SSE-B2) | A second layer; the files are already encrypted by rclone |
| Object Lock | Disable - **decision recorded in section 12a** | It would stop the script pruning dumps older than 30 days; to be revisited before go-live as ransomware protection |

Then **Lifecycle Settings → Keep prior versions for this number of days:
`30`**. With `hard_delete = false` in `rclone.conf` (below), a file deleted
or overwritten stays recoverable for 30 days and is then removed by
Backblaze. "Keep all versions" (the default) would keep everything
forever; "Keep only the last version" would give no way back. A key that
can write can still delete old versions deliberately: this protects
against mistakes, not a determined attacker - that is what Object Lock is
for.

### 11.3 The application key

Application Keys → Add a New Application Key: name `anjal-server-backup`,
**access to the bucket `anjal-backup` only** (not "All"), **Read and
Write**, no file-name prefix, no duration. Never use the master key on
the server, and never press "Generate New Master Application Key" (it
cancels the current one). The applicationKey is shown once: keep the page
open until step 11.5 has written it. It does not go on paper - a new key
can always be made from the account.

### 11.4 The two encryption passwords, onto paper

These two passwords are the only way to read the backup. Generate them,
write both on each custody form, then prove every paper copy by typing it
back (nothing is shown while typing). Stay in the same SSH session until
11.5 is done: the passwords exist only in its memory.

```
vm$ P1=$(openssl rand -base64 18); P2=$(openssl rand -base64 18)
vm$ echo "Backup crypt password 1: $P1"; echo "Backup crypt password 2: $P2"
vm$ clear
vm$ provevar() { local want got c; want=$(printf '%s' "$2" | sha256sum | cut -c1-16); for c in 1 2 3; do read -rs -p "$1 from paper copy $c: " got; echo; if [ "$(printf '%s' "$got" | sha256sum | cut -c1-16)" = "$want" ]; then echo "copy $c: MATCH"; else echo "copy $c: NO MATCH - check this copy"; fi; done; }
vm$ provevar "Crypt password 1" "$P1"; provevar "Crypt password 2" "$P2"
```

Six `MATCH` lines are needed. On 26 September copy 3 of password 2 did
not match; it was corrected against copies 1 and 2 and `provevar` re-run
for that password. Write zero as `Ø` and keep letters clearly apart
(`0`/`O`, `1`/`l`/`I`, upper and lower case, `+` and `/`).

Before rc.7 this section generated each password with
`rclone obscure "$(openssl rand -base64 32)"`, which never shows the
password - only an 80-character scrambled form - while telling the
operator to write the password on paper (DEF-063).

### 11.5 The configuration

The keyID and applicationKey are pasted when asked (the key is not shown):

```
vm$ read -r -p "keyID: " KID; read -rs -p "applicationKey: " AKEY; echo
vm$ O1=$(rclone obscure "$P1"); O2=$(rclone obscure "$P2")
vm$ sudo tee /etc/anjal/rclone.conf >/dev/null <<EOF2
[b2]
type = b2
account = $KID
key = $AKEY
hard_delete = false

[b2crypt]
type = crypt
remote = b2:anjal-backup
filename_encryption = standard
directory_name_encryption = true
password = $O1
password2 = $O2
EOF2
vm$ sudo chown root:anjal /etc/anjal/rclone.conf; sudo chmod 640 /etc/anjal/rclone.conf
vm$ unset P1 P2 O1 O2 KID AKEY
vm$ sudo ls -l /etc/anjal/rclone.conf
vm$ sudo -u anjal rclone --config /etc/anjal/rclone.conf lsd b2crypt: && echo "B2 CONNECTION OK"
```

**Check:** `-rw-r----- 1 root anjal`, then `B2 CONNECTION OK`.

### 11.6 The first run, then the nightly timer

Run once under the real unit; the timer is switched on only if the run
succeeds (`install.sh` leaves it off, and `enable` without `--now` only
arms it for the next boot - DEF-062):

```
vm$ if sudo systemctl start anjal-backup; then echo "BACKUP RUN OK"; OK=1; else echo "BACKUP RUN FAILED - timer left off"; OK=0; fi
vm$ sudo journalctl -u anjal-backup --since "-10min" --no-pager | tail -20
vm$ sudo -u anjal rclone --config /etc/anjal/rclone.conf lsf b2crypt:db/
vm$ sudo -u anjal rclone --config /etc/anjal/rclone.conf size b2crypt:mail/
vm$ echo "files left in scratch: $(sudo ls -A /var/lib/anjal/backup | wc -l)"
vm$ if [ "$OK" = 1 ]; then sudo systemctl enable --now anjal-backup.timer; fi
vm$ systemctl list-timers anjal-backup.timer --no-pager
```

**Check:** `BACKUP RUN OK`; the log shows `dump uploaded and verified`,
`verify ok` and `done`; one `anjal-*.sql.gz` in `db/`; `files left in
scratch: 0`; the timer shows tonight's run (21:00 UTC, 02:30 IST, with up
to 20 minutes of random delay). Measured on 26 September 19:24 IST: 27
seconds, a 12 KB dump, 11 objects (36 KiB) of mail.

The next morning, confirm the first nightly run on its own:

```
vm$ sudo journalctl -u anjal-backup --since yesterday --no-pager | grep -E "verify ok|done|FAIL|ERROR|not configured"
vm$ sudo -u anjal rclone --config /etc/anjal/rclone.conf lsf b2crypt:db/      # one more dump
```

What every run guarantees since rc.6: it refuses to start - before
dumping anything - while `/etc/anjal/rclone.conf` is missing or still
holds `CHANGE-ME`; it deletes the unencrypted local dump however it ends;
and it verifies the dump, the mail and the certificate store with
`rclone cryptcheck`, which compares real checksums through the
encryption. Any difference fails the run (`systemctl --failed` lists
`anjal-backup.service`). Before rc.6 the check compared sizes only and a
detected difference still ended in `done` (DEF-061), and a failed upload
left the unencrypted dump on disk (DEF-062).

Not in the backup, by design: `/etc/anjal/*.env` and `rclone.conf` (their
secrets are on the custody forms - without `ANJAL_KEK` a restored database
cannot unseal the DKIM key), the webmail session keys (everyone signs in
again) and `/var/lib/anjal/greylist.tsv` (senders are greylisted once
more).

---

## 12. Restore drill

A backup nobody has restored is a hope, not a backup. The drill answers
one question: **if this server were lost tonight, could Anjal be rebuilt,
with all its mail, from what survives?** In a disaster only three things
survive, and the drill uses only those:

1. the encrypted backup in the bucket (database, mail, certificates);
2. the **paper custody forms** (database password, admin token, KEK, the
   two backup encryption passwords, the backup account);
3. the release on GitHub and this runbook.

Nothing is copied from the production server. Typing the secrets from
paper takes about 15 minutes of a two-to-three-hour rebuild; the time
goes on building the machine and moving the data.

**When:** a few days after section 11, so several nightly backups exist;
after any change to `backup.sh` or `restore.sh`; before go-live; and at
least twice a year.

**What it proves:** the backup is complete and readable; the paper forms
are complete and correct; the runbook rebuilds a server from nothing; and
the KEK on paper unseals the restored DKIM key. It is also the check for
what CI cannot see - E2E's own image, found only on a real VM.

### 12.1 On production, before the drill

Record what the restored copy must match, and check that nothing is
waiting to be sent (both only read):

```
vm$ date
vm$ psql "host=127.0.0.1 dbname=anjal user=anjal" -Atc "select t.slug, m.local_part||'@'||m.domain, f.name, count(x.id), count(x.id) filter (where x.seen), count(x.id) filter (where x.flagged) from tenants t join mailboxes m on m.tenant_id=t.id join folders f on f.mailbox_id=m.id left join messages x on x.folder_id=f.id group by 1,2,3 order by 1,2,3" | tee ~/drill-counts-production.txt | sha256sum | cut -c1-16
vm$ psql "host=127.0.0.1 dbname=anjal user=anjal" -Atc "select count(*) from outbound_messages where status in (0,1)"
vm$ sudo -u anjal rclone --config /etc/anjal/rclone.conf lsf b2crypt:db/ | tail -1
vm$ sudo journalctl -u anjal-backup --since "-1day" --no-pager | grep -E "verify ok|done"
vm$ curl -s http://127.0.0.1:8025/healthz | jq -r .version
```

Keep the counts file and its 16-character hash; note the newest dump's
name and the release version. **Then run a backup by hand**
(`sudo systemctl start anjal-backup`) so the counts and the newest dump
describe the same moment - mail arriving between them changes the counts.

### 12.2 A drill machine that cannot touch the world

- **E2E:** a node on **hourly** billing (the plan of ANJAL-OPS-08; on
  26 September on-demand was ₹4.48 an hour), Ubuntu 24.04, with a reserved
  IP - E1 nodes have none otherwise, and SSH needs one.
- **A drill-only security group, `anjal-drill`:** inbound **22 only**,
  outbound ALL. No 25, 80, 443, 465 or 587: the drill VM can neither
  receive mail nor be mistaken for the mail server.
- **Sections 1 and 2** as written (hardening, admin account, fail2ban,
  PostgreSQL 18). **Section 3** with the **same release** as production
  (the version recorded in 12.1).

### 12.3 Rebuild from paper

**Section 4, typing every secret from a custody form** - the database
password, the admin token and the KEK - and proving each with `prove` as
section 4 describes. Nothing is copied from production.

Then isolate the drill machine. These settings exist in the product and
are checked against its code; they are the only differences from
production:

```
vm$ S=/etc/anjal/server.env; W=/etc/anjal/webmail.env
vm$ sudo sed -i 's|^ANJAL_BIND=.*|ANJAL_BIND=127.0.0.1|; s|^ANJAL_OUTBOUND_MODE=.*|ANJAL_OUTBOUND_MODE=relay|; s|^ANJAL_ACME_DOMAINS=.*|ANJAL_ACME_DOMAINS=|' $S
vm$ printf 'ANJAL_RELAY_HOST=127.0.0.1\nANJAL_RELAY_PORT=2525\n' | sudo tee -a $S >/dev/null
vm$ sudo sed -i 's|^ANJAL_WEBMAIL_BIND=.*|ANJAL_WEBMAIL_BIND=127.0.0.1|; s|^ANJAL_WEBMAIL_PORT=.*|ANJAL_WEBMAIL_PORT=8080|; s|^ANJAL_WEBMAIL_HTTPS_PORT=.*|ANJAL_WEBMAIL_HTTPS_PORT=0|; s|^ANJAL_ACME_DOMAINS=.*|ANJAL_ACME_DOMAINS=|' $W
vm$ sudo grep -E '^ANJAL_(BIND|OUTBOUND_MODE|RELAY_HOST|RELAY_PORT|ACME_DOMAINS|WEBMAIL_BIND|WEBMAIL_PORT|WEBMAIL_HTTPS_PORT)=' $S $W
```

| Isolation | Setting |
| --- | --- |
| Cannot receive mail | `ANJAL_BIND=127.0.0.1`, and the security group opens 22 only |
| Cannot send mail | `ANJAL_OUTBOUND_MODE=relay` to `127.0.0.1:2525`, where a capture program keeps every message on the machine - including anything left in the restored queue |
| No certificate requests | `ANJAL_ACME_DOMAINS` empty (ACME off) in both files; `ANJAL_WEBMAIL_HTTPS_PORT=0` |
| Webmail not public | `ANJAL_WEBMAIL_BIND=127.0.0.1`, port 8080, reached through an SSH tunnel |

**The backup configuration, from paper.** In the backup account make a
**drill key**: bucket `anjal-backup` only, **Read Only** - a restore needs
nothing more, and the drill then cannot alter a backup. Then write
`rclone.conf` as in 11.5, typing the two encryption passwords from paper
instead of generating them:

```
vm$ read -rs -p "Crypt password 1 (paper): " P1; echo; read -rs -p "Crypt password 2 (paper): " P2; echo
```

followed by the `read ... KID/AKEY`, `rclone obscure`, `tee`, `chown`,
`chmod`, `unset` and `lsd` lines of 11.5. **Check:** `B2 CONNECTION OK`.

### 12.4 Restore, and prove it

With the services stopped (as section 3 leaves them), restore the files
first and then the newest database dump:

```
vm$ sudo /opt/anjal/bin/restore.sh
vm$ sudo -u anjal rclone --config /etc/anjal/rclone.conf lsf b2crypt:db/ | tail -1      # the dump it used
```

Start the capture program - it holds every outgoing message on this
machine - then the services:

```
vm$ sudo apt install -y python3-aiosmtpd python3-dkim
vm$ nohup python3 -m aiosmtpd -n -l 127.0.0.1:2525 -c aiosmtpd.handlers.Mailbox ~/drill-capture >/dev/null 2>&1 &
vm$ sudo systemctl start anjal-server anjal-webmail
vm$ sleep 10; systemctl is-active anjal-server anjal-webmail; curl -s http://127.0.0.1:8025/healthz | jq '{status, version}'
vm$ sudo journalctl -u anjal-server --since "-2min" --no-pager | grep -E "Outbound worker started|Outbound disabled|STARTTLS"
```

Let the capture program create `~/drill-capture` itself: a folder made
beforehand makes it refuse every message, and each refusal would put a
bounce notice into the restored mailbox (measured while writing this
section). **Check:** `active` twice; the release version recorded in
12.1; `Outbound worker started (mode=relay, ...)`.

**1. The counts match** - the same query as 12.1, before anything else
touches the mailbox:

```
vm$ psql "host=127.0.0.1 dbname=anjal user=anjal" -Atc "select t.slug, m.local_part||'@'||m.domain, f.name, count(x.id), count(x.id) filter (where x.seen), count(x.id) filter (where x.flagged) from tenants t join mailboxes m on m.tenant_id=t.id join folders f on f.mailbox_id=m.id left join messages x on x.folder_id=f.id group by 1,2,3 order by 1,2,3" | tee ~/drill-counts-restored.txt | sha256sum | cut -c1-16
```

The hash must equal production's from 12.1. If not, compare the two
files line by line: each line is tenant, mailbox, folder, messages, read,
flagged.

**2. The mail is readable.** From the laptop, open a tunnel and sign in
with the real mailbox password:

```
pc> ssh -i $env:USERPROFILE\.ssh\anjal_e2e -L 8080:127.0.0.1:8080 arun@<drill-ip>
```

Browse to `http://127.0.0.1:8080/`. Open messages from each folder -
the first Gmail message, the Outlook one, the mail-tester report, the
Sent items - and check their read and flagged state.

**3. The KEK from paper unseals the DKIM key.** In the drill webmail,
compose a short message to `drill-check@example.com` and send it. The
server signs it with the restored key and hands it to the capture
program; nothing leaves the machine. Then:

```
vm$ ls ~/drill-capture/new/ | wc -l
vm$ dkimverify < "$(ls -t ~/drill-capture/new/* | head -1)"
```

**Check:** `signature ok`. The key is looked up in public DNS
(`default._domainkey.anjal.co.in`), so a pass proves that the key sealed
in the backup, unsealed with the KEK typed from paper, is the domain's
real key. The whole chain - relay to a capture program, signing,
independent verification - was run and passed on 26 September while
writing this section.

**4. The certificates came back:**

```
vm$ sudo openssl x509 -in /var/lib/anjal/acme/fullchain.pem -noout -subject -enddate
```

Expected not to come back, by design: webmail sessions (sign in again)
and the greylisting memory (senders greylisted once more).

### 12.5 Record, and destroy

Write down the times, the two hashes, the dump used, the release, and
anything that failed or differed from this section. Then **destroy the
drill node and release its reserved IP** - it holds a full copy of the
mail and the certificate's private key - and **delete the drill key** in
the backup account.

---

## 12a. What protects what (for audits)

A summary of the controls this deployment relies on, so an auditor or a
future you can see each one and where it lives.

| Concern | Control | Where |
| --- | --- | --- |
| Network exposure | E2E Security Group only (no host firewall, per E2E's advice); only 22, 25, 80, 443, 465, 587 open; internal services bound to 127.0.0.1 and measured with `ss -tlnp` | sections 1a, 1c |
| Administrative access | Named account per person with sudo; root SSH login, passwords, keyboard-interactive, GSSAPI and X11 off; fail2ban; port 22 to move behind WireGuard before real mail | section 1b, `01-anjal-hardening.conf` |
| Software updates | unattended-upgrades for Ubuntu security and the PostgreSQL repository | sections 1, 2 |
| Admin API | Loopback only, bearer token required, refuses to start otherwise; reached over SSH | `server.env`, section 13 |
| Password storage | PBKDF2-HMAC-SHA256, 600,000 rounds, upgraded on sign-in | application |
| Brute force | SMTP AUTH: 3 per session, 10 per address per 15 min. Webmail: 5 per address+account, 50 per account | application |
| Connection floods | 120 s idle timeout, 15 min session limit, 200 connections, 10 per address | `server.env` |
| DKIM private keys | AES-256-GCM in the database under `ANJAL_KEK`, which lives only in `server.env` and on the handwritten custody forms | section 4 |
| Everything else at rest | Mail, database, and certificates on the VM's disk, which E2E encrypts at rest (chosen when the node was created, no passphrase so it boots unattended) | section 0 |
| Backups | Nightly, encrypted on the server by rclone crypt before upload, verified by checksum (`cryptcheck`) every run; bucket private, key limited to that bucket, deleted files recoverable for 30 days; encryption passwords proven on three paper copies | section 11 |
| Restore | Proven by a drill on a separate machine using only the backup, the paper forms and the release; isolated so it cannot send, receive or request certificates | section 12 |
| Data residency | **Patient data stays in India** (owner's decision, 26 Sep 2026). The server is in E2E's Chennai region. Backblaze B2 (EU Central) holds only encrypted backups of test and operator mail; **before HIS goes live or any mail naming a patient reaches Anjal, whichever is first, backups move to Indian storage (E2E EOS planned) and the Backblaze bucket is deleted** | section 11 |
| Backup immutability | Object Lock **off** (decision of 26 Sep 2026): it would stop the script pruning dumps older than 30 days, and the 30-day version history covers mistakes. It does not stop a determined attacker holding the key; to be revisited before go-live | section 11.2 |
| Changes | Every admin API change and webmail sign-in/password change is recorded in `audit_events`, which the database itself makes append-only | section 13 |
| Service isolation | Dedicated `anjal` user, `ProtectSystem=strict`, `NoNewPrivileges`, restricted address families (`AF_INET`, `AF_INET6`, `AF_UNIX` - no netlink since rc.2) and namespaces | systemd units |
| Webmail session keys | ASP.NET key ring in `/var/lib/anjal/webmail-keys` (anjal 700), unencrypted on the encrypted disk. Encrypting it would need a secret in `webmail.env`, which deliberately holds none; losing it only signs everyone out | `ANJAL_WEBMAIL_KEYS_DIR`, DEF-050 |
| Proven by CI | Every change installs the Linux release with `install.sh`, starts both services under the real units, checks exposure and a real Let's Encrypt staging account | `.github/workflows/deploy-smoke.yml` |

## 13a. Rotating the admin token

The admin token can create mailboxes, read every tenant and hold DKIM keys,
so treat it like a password: keep it out of shared documents, and change it
if anyone who had it no longer needs it. Rotation needs no downtime.

1. Generate the new token on the VM and write it on **new** custody forms,
   version increased by one (the form's "When a secret changes" section):
   `NEW=$(openssl rand -hex 24); echo "$NEW"`.
2. Move the current value to `ANJAL_API_TOKEN_PREVIOUS` and put the new one
   in `ANJAL_API_TOKEN`, from the shell variables - never retyped:
   `OLD=$(sudo grep '^ANJAL_API_TOKEN=' /etc/anjal/server.env | cut -d= -f2-)`, then
   `sudo sed -i "s|^ANJAL_API_TOKEN_PREVIOUS=.*|ANJAL_API_TOKEN_PREVIOUS=$OLD|; s|^ANJAL_API_TOKEN=.*|ANJAL_API_TOKEN=$NEW|" /etc/anjal/server.env; clear`.
   Prove the new paper copies with `prove ANJAL_API_TOKEN` (section 4).
3. `sudo systemctl restart anjal-server`. Both tokens now work, and the log
   says so at start-up.
4. Update every caller (SIGMA, Lipi, your own scripts) to the new token.
5. Confirm nothing still uses the old one, then clear
   `ANJAL_API_TOKEN_PREVIOUS` and restart again. Replace all three paper
   copies and destroy the old sheets; `unset OLD NEW`.

Check each step with a call that needs the token:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $NEW" http://127.0.0.1:8025/api/tenants   # 200
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $OLD" http://127.0.0.1:8025/api/tenants   # 200 until step 5, then 401
```

The token is never written to the log, so a log extract can be shared
without redacting it.

## 13b. Mailbox passwords

One rule applies to the webmail, the admin API and SMTP service accounts:
at least 8 characters with an upper-case letter, a lower-case letter, a
number and a symbol - or a phrase of 16 characters or more, which needs
none of those. Both are checked against a list of predictable choices, so
`Apulki@123` and `Passw0rd!` are refused however well they satisfy the
rule, as is anything containing the person's own name or address.

## 13c. Unknown recipients

Mail for an address that does not exist on one of your domains is refused
at `RCPT TO`, before the message is transferred, so a misaddressed 20 MB
report costs nothing and the sender is told at once. To go back to
accepting it and refusing after the message arrives - which tells a
stranger less about which addresses exist - set
`ANJAL_SMTP_LATE_RECIPIENT_CHECK=true`.

## 13d. Upgrading to a new release

```
pc> git checkout vX.Y.Z; .\deploy\publish.ps1 -Version X.Y.Z; scp ... arun@203.0.113.10:~/
vm$ sha256sum anjal-X.Y.Z.tar.gz | cut -c1-16                 # the laptop's value
vm$ tar -xzf anjal-X.Y.Z.tar.gz
vm$ sudo bash ./anjal-X.Y.Z/bin/install.sh ~/anjal-X.Y.Z       # keeps the previous binaries as *.old
vm$ psql "host=127.0.0.1 dbname=anjal user=anjal" -v ON_ERROR_STOP=1 -f ~/anjal-X.Y.Z/bin/schema.sql
vm$ sudo systemctl restart anjal-server anjal-webmail
vm$ sleep 10; systemctl is-active anjal-server anjal-webmail; curl -s http://127.0.0.1:8025/healthz | jq .
```

`install.sh` never overwrites `/etc/anjal/*.env`. When a release adds a
setting to the templates, add it by hand; the release note says which.

**rc.1 to rc.2 on the first server** (26 September 2026), in addition:

1. Remove the DEF-048 workaround, so the unit's sandbox is back to what it
   ships as: `sudo rm -r /etc/systemd/system/anjal-server.service.d && sudo systemctl daemon-reload`,
   then `systemctl show anjal-server -p RestrictAddressFamilies` must list
   `AF_INET AF_INET6 AF_UNIX` only.
2. Add the new webmail setting (DEF-050):
   `echo 'ANJAL_WEBMAIL_KEYS_DIR=/var/lib/anjal/webmail-keys' | sudo tee -a /etc/anjal/webmail.env`.
   Keys previously kept in `/var/lib/anjal/.aspnet` are no longer used;
   anyone signed in signs in once more.
3. Add the fail2ban tuning (section 1) and the `Subsystem` line (section 1b)
   if they are not already present.

**rc.5 to rc.6**, in addition: the backup timer was enabled by every
earlier `install.sh` and comes alive at the next boot (DEF-062). Until
section 11 is done, switch it off:
`sudo systemctl disable anjal-backup.timer` - `systemctl is-enabled anjal-backup.timer`
must print `disabled`.

**rc.4 to rc.5**, in addition:

1. The `schema.sql` step above adds one column, `messages.spam_checked`
   (decision 1B). Messages stored before it are left empty and the webmail
   decides by folder. **Check:**
   `psql "host=127.0.0.1 dbname=anjal user=anjal" -Atc "select count(*) from information_schema.columns where table_name='messages' and column_name='spam_checked'"`
   prints `1`.
2. Greylisting's new settings (decision 2B) work from their defaults; add
   them to `/etc/anjal/server.env` only to change them (the template lists
   all four). **Check:** the server's start-up line reads
   `Greylisting: on (delay 300s, senders remembered 35 days, kept in /var/lib/anjal/greylist.tsv, senders passing SPF not delayed).`
   The state file appears after the first greylisting decision.

---

## 13. Operating

| Task | Command |
|---|---|
| Logs | `journalctl -u anjal-server -f`, `journalctl -u anjal-webmail -f` |
| Health | `curl -s http://127.0.0.1:8025/healthz \| jq .` |
| Metrics | `curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/metrics` |
| Certificate status | `curl -s -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/acme \| jq .` |
| Force renewal | `curl -s -X POST -d '' -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8025/api/acme/renew` (the `-d ''` matters - section 7) |
| Upgrade | section 13d |
| Roll back | `sudo mv /opt/anjal/server.old /opt/anjal/server` (same for webmail) → restart |
| Add a mailbox | `POST /api/mailboxes` (section 8) |
| Block a sender for a tenant | `POST /api/tenants/imagiqa/sender-rules -d '{"pattern":"@spammer.example","action":"block"}'` |
| Spam threshold | `POST /api/tenants -d '{"slug":"imagiqa","displayName":"imagiQa","spamThreshold":5}'` |
| Audit trail | `curl -s -H "Authorization: Bearer $TOKEN" "http://127.0.0.1:8025/api/audit?limit=50" \| jq .` |
| Webhook queue | `/metrics` → `anjal_webhook_pending`; `anjal_webhook_failed_total` should stay at 0 |

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
| Webmail unreachable | Security Group rules, `sudo ss -tlnp \| grep ':443'`, `journalctl -u anjal-webmail` | port 443 missing from the Security Group, or service failed to bind (must run with `CAP_NET_BIND_SERVICE` - the unit sets it) |
| `/healthz` `tls` degraded, "no certificate" | `journalctl -u anjal-webmail \| grep ACME` | challenge failed: DNS, port 80, or PTR; fix and restart webmail |
| Inbound mail never arrives | `journalctl -u anjal-server \| grep -i "rcpt\|relaying"` | MX not pointing here, or domain not registered to a tenant (`GET /api/tenant-domains`) |
| Inbound lands in Junk | message page → spam reasons | see section 13; `SPF_NONE`/`DKIM_NONE` from a big provider means your DNS resolver on the VM is failing - check `resolvectl status` |
| Outbound stuck | `/metrics` `anjal_outbound_pending`, then `nc -vz gmail-smtp-in.l.google.com 25` from the VM | outbound 25 blocked in the Security Group or by E2E (sections 0, 1a), or recipient greylisting you (normal, retries) |
| Gmail shows `DKIM: FAIL` | section 9 TXT record | public key mismatch, selector typo, or TXT split into wrong chunks by the DNS panel |
| `452 4.2.2 Mailbox full` in logs | webmail sidebar usage | quota reached; delete mail or raise `quotaBytes` via `POST /api/mailboxes` |
| Server stops at once: `NetworkInformationException (97): Address family not supported by protocol` | `journalctl -u anjal-server` | release older than rc.2 (DEF-048). Upgrade; the rc.1 workaround was a drop-in adding `RestrictAddressFamilies=AF_NETLINK` |
| ACME: `newAccount failed: 400 ... Invalid Content-Type header on POST` | `journalctl -u anjal-webmail \| grep ACME` | release older than rc.2 (DEF-049); no configuration works around it - upgrade |
| API answers `411 Length Required` | the `curl` command | a POST without a body needs `-d ''`; since rc.3 nothing was changed - run it again with `-d ''` (up to rc.2 the change was made anyway, DEF-053) |
| Production still uses the staging account after the switch | `sudo ls -la /var/lib/anjal/acme` | the folder was not emptied; `sudo rm -rf .../acme/*` expands nothing as your own user - use `sudo find /var/lib/anjal/acme -mindepth 1 -delete` (DEF-052) |
| `scp` logs in, then only `Connection closed` | `sudo sshd -T \| grep subsystem`, `journalctl -u ssh` | SFTP helper path wrong on E2E images; section 1b `Subsystem` line |
| Backup fails | `journalctl -u anjal-backup` | rclone config unreadable by `anjal` (mode 640, group anjal), wrong B2 key, or `pg_dump` cannot connect (`ANJAL_POSTGRES` in `server.env`) |
