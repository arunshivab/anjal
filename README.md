# Anjal (அஞ்சல்)

A .NET 10 mail server library. Protocol code (MIME, SMTP, DNS, DKIM, SPF,
DMARC) and the HTTP API are hand-written with no external NuGet
dependencies. TLS uses the BCL's `System.Net.Security.SslStream`. DKIM
and DMARC signature verification use the BCL's
`System.Security.Cryptography.RSA`. Password hashing uses the BCL's
`Rfc2898DeriveBytes.Pbkdf2`. The PostgreSQL store layer uses
[Npgsql](https://www.npgsql.org/).

## Status

**v1.0.0-rc.15** (11 October 2026) - the webmail release: rc.11 to rc.15 built
and evaluated as one (ANJAL-SPEC-11, SPEC-11B v1.1, DES-11, decisions D-111 to
D-148 in ANJAL-PRJ-03c). The "every script" look in 21 colours with light and
dark; the letter and envelope layout with three panes, Focus and a phone
layout; no full page reloads and live new mail with one chiming tab; compose
in the letter or docked, templates, Send one each, Send later; contacts,
rules, categories and conversations; sign-in with two-step (authenticator,
passkeys, backup codes), invitations, password reset and sign-in alerts; the
organisation console and the Anjal console with dashboards, storage plans,
operator alerts and service health; the mail list kept offline, locked; six
languages ready (English switched on). Passwords follow NIST SP 800-63B-4: 15
characters alone or 12 with two-step, no character-type rule, no forced
change, leaked passwords refused. A failed backup is retried for about 30
minutes, then reported at once (D-88). Three control heights across the app.
Upgrade notes: DEPLOY.md 13d.

**v1.0.0-rc.10** (2 October 2026) - the open defects and MTA-STS (ANJAL-SPEC-10).
After Back, a page the browser restores from its back-forward cache reloads, so
a message just read no longer shows as unread (DEF-078). A message whose content
is only attachments - Google's DMARC reports are a single zip - says so instead
of showing its raw headers (DEF-079). The start-up log says where postmaster@
and abuse@ at the server's own name go (DEF-082). The webmail serves the
MTA-STS policy for each hosted domain, built from settings, off until enabled
(DEPLOY.md 13i). A changed ANJAL_ACME_EMAIL now reaches the existing Let's
Encrypt account, at the next certificate issue (renewal or --acme-renew-now). Test output prints the evidence line once.

**v1.0.0-rc.9.2** (1 October 2026) - hotfix from the first restore drill:
restore.sh restores the database again (DEF-083, critical). It read
/etc/anjal/server.env through bash, where the ';' in ANJAL_POSTGRES
separates commands, so it connected as root and never restored a database;
backups were never affected (systemd reads the file literally). It now reads
the setting as text and proves the connection before deciding anything; CI
restores a real backup into a real database with it. The webmail offers
browsers TLS 1.3, or TLS 1.2 with ECDHE and GCM or ChaCha20 only - the two
CBC suites SSL Labs marked weak are gone (DEF-084). The runbook carries the
drill's eleven corrections; new installations get hex backup passwords.

**Runbook update after v1.0.0-rc.9.1** (documentation only; production
stays on rc.9.1). Section 12, the restore drill, brought up to date with
what the backup now holds: the production record and a manual backup are
taken together at a quiet moment; the proofs add the evidence (records,
files read-only, manifest chain verified), greylisting memory (kept in the
database since rc.7, so it comes back) and the settings snapshot, and run
in an order where nothing on the drill machine can create mail before the
counts are compared; results go on a drill record form (ANJAL-OPS-02).
Section 12a corrected (host firewall since 27 Sep, 300 s idle timeout)
and extended (evidence, DNS authentication, postmaster@ and abuse@).

**v1.0.0-rc.9.1** (29 September 2026) - hotfix: SPF counted DNS lookups wrongly
(DEF-081). Every include was counted twice, so a sender publishing five
includes - the structure Microsoft 365 uses - reached 11 and got a PermError
instead of Pass. Consequences in rc.9: such senders were greylisted and could
loop (each retry comes from another address); a sender without DKIM whose
domain has DMARC p=reject would have been refused. Counting now follows
RFC 7208 4.6.4; Authentication-Results now gives the reason for a PermError or
TempError.

**v1.0.0-rc.9** (28 September 2026) - standards fixes from the compliance
audit (ANJAL-TST-12), and the unread count.
- **Delivery:** a domain with no MX record is delivered to its own address
  (RFC 5321 5.1, DEF-067); a null MX fails at once (RFC 7505, DEF-071);
  outgoing mail is retried for 5 days, every 6 hours after the first hours
  (DEF-068) - an API caller's own give-up time is kept.
- **Receiving:** every reply carries an enhanced status code, PIPELINING and
  ENHANCEDSTATUSCODES are advertised, EXPN answers 502 (DEF-074); parameters
  that are not advertised are refused with 555 (DEF-070); the idle timeout is
  5 minutes (DEF-072); final delivery adds Return-Path (DEF-069).
- **Submission:** a missing Date or Message-ID is added (RFC 6409, DEF-073);
  a local recipient's copy keeps its evidence link (DEF-077).
- **Webmail:** opening an unread message lowers the INBOX count at once
  (DEF-066); a message sent from the webmail keeps its original as composed,
  with the colleague's copy and the Sent copy linked to it - and is not sent
  if the original cannot be kept (DEF-076).

**v1.0.0-rc.8** (28 September 2026) - the integrity release: the original of
every message kept as proof, and records that tell the truth.
- **Evidence store** (ANJAL-DES-01): every incoming message is kept exactly as
  received - before any repair or addition - and every outgoing message exactly
  as sent (signed once; every retry sends the same bytes), each with its
  SHA-256, and every delivery attempt with the receiving server's reply. A
  message is not accepted, nor sent, unless its original is kept. A daily
  manifest, chained to the one before, lists every copy added and purged; the
  admin API verifies the chain and every file, and returns an original only
  after re-hashing it. Deleting mail leaves the original for the tenant's
  retention (3 years by default); backups copy evidence append-only.
- **DEF-065:** the spam filter dropped how a message arrived, so all incoming
  mail since rc.7 showed "arrived unencrypted". Fixed; past labels are
  recovered from each message's own Received line (admin API, dry run first).
  Mail stored before rc.8 can be given evidence copies marked *reconstructed*.
- **postmaster@ and abuse@** every domain, and bare `<postmaster>`, are
  accepted (RFC 5321, RFC 2142) and go to a designated mailbox.
- **Outbound firewall** from incident ANJAL-INC-01 is in the release and the
  runbook (DEPLOY.md 13f): only Anjal may send mail from the server.
- Dates Anjal writes are in India time with their true offset
  (`ANJAL_TIMEZONE`); delivery log lines name the server and the TLS used.
- The webmail shows "Original kept" with the fingerprint, and says the
  original stays when mail is deleted permanently.

**v1.0.0-rc.7** (27 September 2026) - encryption both ways, and memory
that survives a rebuild.
- **DEF-064:** mail to rediffmail.com stayed queued - its MX offers only DHE
  and static-RSA ciphers, and .NET on Linux offers neither by default.
  Anjal now offers TLS 1.3, ECDHE, DHE and static RSA with AES-GCM, in that
  order (what Gmail and Yahoo use to reach it), and never 3DES, RC4, CBC,
  NULL or export. A handshake failure logs its real cause.
- **Never unencrypted** (owner's decision): a message is held, retried and
  returned to its sender rather than sent without encryption - including to
  servers that offer no STARTTLS, which earlier releases served in plain text.
- **Incoming:** each message records whether it arrived encrypted and how;
  unencrypted mail shows a red open lock and **Trust this sender**; a tenant
  may file it in its own folder.
- **Greylisting memory** in the database, mirrored to the file and backed up.
- **Settings in the database** (imported from the env files once, changed
  through `/api/settings`, audited; secrets never stored), and a
  secrets-removed copy of the env files in every backup.
- The restore drill now writes its isolation into the restored database.
- **Webmail over slow routes:** `HEAD` is answered (it returned 405), and
  stylesheets, scripts and SVG are compressed (Brotli, else gzip) - built
  into .NET, nothing external. Pages are never compressed: they carry
  security tokens and mail (BREACH). Static files were already cached by
  browsers for a year under versioned names.
- **HTTP/3** on the webmail's HTTPS port (QUIC: one round trip to connect,
  and a lost packet no longer stalls the whole page), with Microsoft's
  libmsquic and UDP 443 (DEPLOY.md section 1d). Without them the webmail
  says so and serves HTTP/1.1 and HTTP/2 as before.

**Runbook update after v1.0.0-rc.6** (documentation only; production
stays on rc.6). Section 11 rewritten from the first real backup setup on
26 September 2026: the backup account, bucket, lifecycle and bucket-limited
key; the two encryption passwords shown once, written on the custody
forms and proven before use (DEF-063: the old commands never showed them);
`hard_delete = false` with a 30-day version history. Section 12 is now the
full restore drill - rebuilt only from the backup, the paper forms and the
release, on a machine isolated from mail and certificates, with a count
comparison and a DKIM proof that the KEK on paper unseals the restored key.
Section 12a records the data-residency rule (patient data stays in India;
Backblaze only until HIS or patient mail) and the Object Lock decision.

**v1.0.0-rc.6** - backups that can be trusted, found by reviewing section 11
before first use. DEF-061: the nightly verification compared file sizes
only - through rclone's encryption `rclone check` has no checksum in
common - so a damaged backup was reported "verify ok"; and it was written
"check && log", so under set -e even a detected difference ended in "done"
and success. Now `rclone cryptcheck` verifies the dump, the mail and the
certificate store, and any difference fails the run. DEF-062: install.sh
enabled the backup timer at install, so from the next boot it would run
nightly against the unconfigured template; and whenever an upload failed
the unencrypted database dump stayed on disk. Now the script refuses to
start until backups are configured, removes the dump however it ends, and
the timer is switched on in section 11 after a verified first run.
deploy-smoke runs a backup under its real unit on every change - refused,
verified, and failed on a backup damaged at rest. Upgrade: section 13d,
"rc.5 to rc.6".

Previously: **v1.0.0-rc.5** - the owner's decisions after the first real mail, and two
display defects. Decision 1B: the webmail says whether a message went
through the incoming checks - a new `messages.spam_checked` column records it
at delivery, the list shows 0 for checked mail and a dash for unchecked, and
the user's own sent mail says so instead of "Spam score 0". Decision 2B:
greylisting no longer delays a sender whose IP passes SPF for its domain,
remembers senders for 35 days instead of 36 hours, keeps them across
restarts in `/var/lib/anjal/greylist.tsv`, and logs every decision; the first
Gmail and Outlook messages had waited 25 and 34 minutes, and no deferral was
ever logged (DEF-059). DEF-060: on phones every message took about four
stacked bands, because desktop rules for the list cells came later in the
stylesheet than the phone layout; now a compact two-line row, measured at
73px. On a computer the reading page fits the window - the message box
scrolls inside itself and the page does not - and message text is 14px; the
message frame's sandbox is unchanged. Upgrade: section 13d, "rc.4 to rc.5".

Previously: **v1.0.0-rc.4** - DKIM made to agree with the rest of the world. The first
real inbound message, from Gmail, failed DKIM on the production server
although Gmail's signature was valid. DEF-056: Gmail over-signs - it lists
From, To, Subject and other headers twice, and names absent ones such as Cc -
and the verifier added a header again for every listing instead of taking
instances from the bottom up and contributing nothing once they ran out
(RFC 6376 section 5.4.2); it also hashed headers under the spelling in h=
rather than as written, which broke simple canonicalization. DEF-057: only
the first DKIM-Signature was checked, so a broken or unaligned first
signature hid a valid aligned one - enough for DMARC to reject legitimate
mail from a p=reject domain. DEF-058: in simple mode the signer hashed the
DKIM-Signature header without the space it then wrote after the colon, so
receivers rejected every simple-mode signature; relaxed mode, which the
server uses, was unaffected and its signatures are byte-for-byte unchanged.
Anjal had only ever been tested against itself: the new tests use messages
signed and judged by dkimpy, an independent implementation. The runbook's
section 8 no longer writes the DKIM key where other accounts can read it
(DEF-054) or puts the mailbox password on the command line (DEF-055).

Previously: **v1.0.0-rc.3** - two defects found while taking the production
certificate. DEF-053: an admin POST sent without a body (`curl -X POST`
with no `-d`) was answered `411 Length Required` by the HTTP listener, which
then passed the request to the API anyway - the change was made while the
caller was told it had failed, and a caller who retried would request a
new certificate each time. Such a request is now refused before anything
runs, and deploy-smoke proves it under the real unit. DEF-052: the
runbook's `sudo rm -rf /var/lib/anjal/acme/*` removed nothing, because the
caller's own shell cannot expand a path inside the service's private
folder; section 7 now uses `find ... -delete`. The runbook reads the admin
token into `$TOKEN` from `server.env` instead of asking for it to be pasted,
rotates it by the custody-form method, and records the certificate
procedure exactly as measured on the first server.

Previously: **v1.0.0-rc.2** - fixes everything the first real start found on the
production server (phase B), and closes the gaps that let it through.
DEF-048: the mail server aborted at startup because finding the system DNS
server enumerated network interfaces over netlink, which the systemd unit's
sandbox forbids; the server list is now read from `/etc/resolv.conf` and the
lookup can no longer throw. DEF-049: Let's Encrypt refused every request
because the ACME client sent `application/jose+json; charset=utf-8`; the
header is now exact, and the test fake compares it as strictly as Let's
Encrypt does. DEF-050: the keys that sign webmail sessions are kept in an
explicit folder (`ANJAL_WEBMAIL_KEYS_DIR`) instead of wherever ASP.NET
chose. DEF-051: the DNS test project had been outside the solution since
May, so it never ran and had stopped compiling; it runs again, and CI now
fails if any project is left out. A new `deploy-smoke` workflow installs
the Linux release with `install.sh` and starts both services under the real
systemd units on every change, including a real Let's Encrypt staging
account - the conditions under which DEF-048 and DEF-049 appeared. Also:
startup messages no longer ask for `ANJAL_TLS_CERT_PATH` while an ACME
certificate is awaited; the runbook adds the SFTP `Subsystem` fix for E2E
images, fail2ban's aggressive mode, `bash install.sh`, and an upgrade
section.

Previously: **v1.0.0-rc.1** - the first release candidate for 1.0, and the build that
goes onto the production server for phase B testing. 1.0.0 itself is tagged
only after phase B and a restore drill pass on this code. Fixes DEF-047: a
mailbox address sent percent-encoded in the URL (`arun%40anjal.co.in`, as
PowerShell and most HTTP clients encode it) was refused with 400 on every
mailbox route; the address segment is now decoded after the path is split,
so an encoded slash cannot reach another route. The deployment runbook's
sections 0 to 5 are rewritten from the first real provisioning on E2E: a
named admin account with root SSH login off, the Security Group as the only
firewall, PostgreSQL 18 from the PostgreSQL repository, self-service PTR,
secrets generated on the VM and proven onto handwritten custody forms, and
20 tables where it said 15.

Previously: **v0.18.3** - CodeQL now analyses only the code that ships. A paths-ignore
filter does not exclude anything in a compiled language - CodeQL sees
whatever the build compiles - so seven alerts about a deliberately careless
ACME test fake appeared on the first run. The workflow builds src and
examples only. Also: the line endings of eight test project files are
normalised; they were stored with CRLF by an automated dependency update,
against this repository's convention, which left them showing as modified in
every clone.

Previously: **v0.18.2** - **CodeQL and Dependabot**. GitHub's analysis engine now runs on
every change with the security-extended queries, which include the
inefficient-regular-expression checks that DEF-044 belonged to; test code is
excluded, since the fakes there behave carelessly on purpose. Dependabot
proposes dependency updates weekly, with major versions left as a deliberate
decision. Running CodeQL over v0.18.1 produced one finding in product code -
the query string flowing into the HTTPS redirect. It was a false positive,
the host having been validated since v0.16.0, but the URL construction is now
a named function with eight tests covering forged Host headers, lookalike
domains, "//host" paths and hostile query strings, and it collapses a leading
"//" so a path can never be read as a host.

Previously: **v0.18.1** - the admin token must have some variety, not merely length: a
value of forty identical characters passed the v0.18.0 checks. Our own QA
helper produced exactly that on Windows PowerShell 5.1, where the .NET Core
API it used left its buffer zeroed and the failure was easy to miss.

Previously: **v0.18.0** - **security work from the v0.17.1 review and an in-house audit
pass**. Unknown recipients on our own domains are refused at RCPT TO instead
of after the whole message is transferred, so a misaddressed 20 MB report
costs one line and the sender is told at once (set
ANJAL_SMTP_LATE_RECIPIENT_CHECK=true for the old behaviour). One password
rule now covers the webmail, the admin API and SMTP accounts, where three
different rules applied before: eight characters with upper case, lower
case, a number and a symbol, or a phrase of sixteen or more; both checked
against predictable choices, so "Apulki@123" is refused however well it
satisfies the rule. The admin token may be rotated without downtime through
ANJAL_API_TOKEN_PREVIOUS, and the server refuses to start with a token under
24 characters or an obvious placeholder - including the one in our own
deployment template. Also: dependency and secret scanning on every build, a
swept inventory of every regular expression in the product, and 40,000
fuzzed messages through the MIME parser.

Previously: **v0.17.3** - **SEC-R1: a denial of service in the HTML sanitiser**, found
by a security review of v0.17.1. A message body containing a tag start
followed by a long run of whitespace and no ">" cost time proportional to
the square of that run - two seconds for 40,000 spaces, over thirty for
200,000 - and it ran when the reader opened the message, so anyone able to
send mail could peg a CPU core and, with a few such messages, take the
webmail down. The sanitiser's patterns now use .NET's non-backtracking
engine, which is linear whatever the input: two million spaces in a
hundredth of a second, with identical results on ordinary markup. Bodies
are also capped at 512 KB (the reader is told when one is shortened), every
pattern has a two-second timeout, and if one ever fires the message is shown
as plain text rather than passed through unchecked.

Previously: **v0.17.2** - **two findings from the v0.17.1 retest**. The admin API now
checks a mailbox address before it writes anything: the local part becomes a
directory name, and one that the filesystem rejects used to commit the
database row first and then fail, leaving an enabled mailbox with nowhere to
put mail. Malformed addresses are refused with 400, storage is created before
the row, and ordinary addresses are unaffected. In the plain-text copy of a
formatted message, a paragraph after a numbered list starts on its own line
again.

Previously: **v0.17.1** - **fixes from QA phases A1, A2 and A3 of v0.17.0**. Subjects
written in Tamil, Hindi, Malayalam, Arabic, Japanese and other scripts
without capital letters are no longer scored as though they were shouting:
the check now counts only cased letters, so it penalised ordinary mail in
the languages Anjal is built for. When the database is unreachable the
webmail now shows a page saying the service is briefly unavailable, with a
reference that matches one line in the log, instead of an empty 500; and
/healthz, which needs no token, reports "unreachable" rather than naming the
driver, host and port. Mail from the
webmail to a mailbox on the same server is now delivered directly; it was
queued outbound to loop back through the server's own MX, which never
arrived on a server without one. The message view shows the SPF, DKIM and
DMARC verdicts this server recorded on arrival - and only those: the MTA now
removes any incoming Authentication-Results header claiming this server's
name, so a sender cannot plant a "dmarc=pass". Numbered lists keep their
numbers in the plain-text part; malformed sender-rule domains such as "@."
are refused (in the webmail and the admin API alike); the bulk action bar
wraps at any width; the Received trace line is the first line of stored mail.

Previously: **v0.17.0** - **formatted mail, signatures, and the fixes from QA phase A1
and the owner's review**; every change reproduced by a test first and checked
on PostgreSQL. Compose, reply and forward gain a formatting toolbar (bold,
italic, underline, lists, links, quotes); mail goes out as HTML with a
plain-text part derived on the server from the sanitised HTML, and falls back
to plain text without JavaScript. Signatures, formatted, are added once to new
messages, replies and forwards. Settings is in two panes with a section each
for profile, appearance, categories, senders, signature and password; sender
rules can be created there directly. Two sanitizer faults are fixed: HTML
mail whose head held a meta or link element rendered blank (Outlook sends
one in every message), and a doctype appeared as text. Report spam and Not
spam are personal, no longer tenant-wide. Search matches message bodies.
Forward and reopened drafts keep their attachments. Trash can restore, delete
permanently and be emptied. Lists mark attachments, align their columns, and
show recipients in Sent and Drafts; the message view puts attachments first
and the category under the subject. Missing pages are 404s with their page;
mark-all-read requires the antiforgery token; workers log an outage once.

Previously: **v0.16.1** - **fixes from manual QA**. Running Anjal against PostgreSQL
for the first time, while writing the QA plan, found defects the automated
suite could not see; all are fixed and covered by new tests. A database
outage now makes the MTA defer (451) instead of refusing mail permanently.
Mail submitted on ports 587 and 465 reaches outside addresses through the
outbound queue, DKIM-signed, with a copy filed in Sent. The dashboard no
longer fails with PostgreSQL, and a new test harness renders every page
against an asynchronous store so that class of fault is caught in CI.
Malformed page and message ids in URLs no longer cause an error. Every
module reports the real version from Directory.Build.props, publish.ps1
stamps it into the binaries and refuses to build a version that does not
match the source or its tag, and the MTA listener's diagnostic log is wired.

Previously: **v0.16.0** - **security hardening** from two independent audits, every
finding fixed and covered by a regression test. The SMTP read path is
rebuilt: buffered, with an idle timeout, a hard session lifetime, global
and per-address connection caps, overlong-line and oversize-DATA draining,
RFC 1870 SIZE enforcement at MAIL FROM, and refusal of any lone "." line
wrapped in non-CRLF endings (SMTP smuggling). MIME nesting and part counts
are bounded, so a crafted message can no longer crash the process. Header
values are validated at the type boundary, closing a header-injection path
that DKIM would have signed. AUTH and webmail sign-in are throttled;
PBKDF2 is at 600,000 rounds with upgrade-on-login and constant-time
misses. Outbound leases expire and are reclaimed; failed outbound mail
produces an RFC 3464 bounce in the sender's INBOX; webhooks move to a
durable, retried queue. DKIM verification follows RFC 8301; SPF enforces
the void-lookup limit; DNS replies must come from the server asked. DKIM
private keys are sealed at rest with AES-256-GCM under `ANJAL_KEK`; an
append-only audit trail (enforced by a database trigger) records every
admin change and webmail sign-in. The webmail sends a strict content
security policy and the usual security headers, its sanitizer parses tags
the way browsers do, webhooks are SSRF-guarded on the address actually
dialled, and the admin API refuses to run without a token or on a public
address. Port 465 (implicit TLS) is available. Opportunistic outbound TLS
no longer fails delivery on self-signed MX certificates (RFC 7435); domains
that require TLS are validated with revocation checking.

Previously: **v0.15.0** - everything below plus **categories and the dashboard**.
Categories exist at two levels: a tenant's defaults, shared by every
mailbox and the only ones an institution can report across, and a
mailbox's own additions, private to it. Eight colour slots are a
per-mailbox budget - the tenant's take theirs first, a mailbox's own take
what remains, and any beyond eight work by name alone. Assigning a
category can remember the sender, so later mail from them is filed at
delivery. The dashboard reports one mailbox over a period: delivered with
the Junk count beside it, sent and received per day, received by folder
*and* by category, top senders, attachments - every figure repeated as a
table so nothing is locked behind a colour. `deploy/DEPLOY.md` now covers
E2E's two firewalls (Security Group and firewalld) instead of ufw.

Previously: **v0.14.0** - the **designed webmail**: the
Anjal design system (four first-class themes, LiPi Sans covering Latin
and nine Indic scripts, LiPicons, the seal), and the features a mailbox
is actually lived in through — drafts with autosave, reply / reply all /
forward with plain-text quoting, Bcc, server-side search across one
folder or all, per-mailbox settings (display name, theme, password),
bulk actions and mark-all-read, unread counts, and address suggestions
learned from Sent. Six small JavaScript enhancements sit on top of pages
that work fully without them. A disabled tenant now defers inbound mail
with `450` instead of rejecting it, so a suspension bounces nothing.

Previously: **v0.13.0** - everything below plus **deployment hardening**: hard
mailbox quota (`452 4.2.2 Mailbox full` at RCPT, compose disabled in
the webmail when full), an unauthenticated `/healthz` (store, Maildir,
certificate) and an authenticated Prometheus `/metrics`, and a
`deploy/` folder with systemd units, env templates, an installer, an
rclone-to-Backblaze backup with restore, a self-contained publish script
and a step-by-step runbook (`deploy/DEPLOY.md`) from fresh Ubuntu VM to
first mail. This is the last release before the first production
deployment.

Previously: **v0.12.0** - full SMTP server with bidirectional mail + DKIM signing +
SPF/DKIM/DMARC inbound verification + SMTP submission authentication on
dual-port (25/587) with open-relay guard + multi-tenant mailbox storage +
webmail + minimum-heuristics anti-spam + **built-in ACME (Let's Encrypt)
certificates**. `Anjal.Acme` is an RFC 8555 client with no external
dependencies: it creates the account, answers HTTP-01 challenges from
the webmail's own HTTP listener (or a small responder in the server),
obtains the certificate, renews it 30 days before expiry, and both the
SMTP listeners and the webmail pick up a renewed certificate on the next
connection without a restart. One certificate covers the SMTP host name
and the webmail. `POST /api/acme/renew` (or `--acme-renew-now`) forces a
renewal so the whole path can be exercised on day one rather than
discovered at day 60.

Previously: **v0.11.0** - `Anjal.Spam` scores every
unauthenticated delivery (authentication results, sender/HELO/reverse-DNS
sanity, header hygiene, a short phrase list, recipient count), writes
the verdict into `X-Anjal-Spam-Score` / `X-Anjal-Spam-Reasons` headers,
and the mailbox sink files mail at or above the tenant's threshold in
**Junk** instead of INBOX. Per-tenant sender allow/block rules override
the score; the webmail's *Not spam* / *Report spam* buttons create them.
The MTA port also gets connection and message rate limits and classic
greylisting. Nothing is rejected on the strength of a score unless
`ANJAL_SPAM_ACTION=reject` is set deliberately - v1 is designed to be
observed before it is trusted.

Anti-spam is the third step of the arc
(storage -> webmail -> anti-spam -> deployment hardening). Not yet in
this release: Bayesian/corpus filtering, DNSBL lookups, IMAP/POP, hard
quota enforcement, search, HTML compose, self-service domain verification.

## Modules

| Module | Purpose | NuGet |
|---|---|---|
| `Anjal.Mime` | MIME parser and builder (RFC 5322, RFC 2045-2049) | none |
| `Anjal.Smtp` | SMTP receiver and sender (RFC 5321), STARTTLS (RFC 3207), AUTH PLAIN/LOGIN (RFC 4954), PBKDF2 password hasher | none |
| `Anjal.Dns` | DNS MX + TXT resolver (RFC 1035) | none |
| `Anjal.Dkim` | DKIM signer (RFC 6376), RSA-SHA256 | none |
| `Anjal.Auth` | SPF (RFC 7208) + DKIM verifier (RFC 6376) + DMARC (RFC 7489) + Authentication-Results (RFC 8601) | none |
| `Anjal.Routing` | Inbound routing + signed webhook dispatcher | none |
| `Anjal.Store` | Persistence: in-memory and PostgreSQL | Npgsql |
| `Anjal.Mailbox` | Multi-tenant Maildir storage (tmp/new/cur, Maildir++ folders, flag renames, moves) and the mailbox delivery sink | none |
| `Anjal.Webmail` | Webmail host process (Blazor static SSR on Kestrel, cookie auth, HTML sanitiser) | none (ASP.NET Core shared framework) |
| `Anjal.Spam` | Spam scoring, sender rules, rate limiting and greylisting | none |
| `Anjal.Acme` | RFC 8555 client: account, orders, HTTP-01, CSR, PEM store, renewal scheduler, hot-reload watcher | none |

The webmail carries the design system as embedded assets - `tokens.css`,
`app.css`, `app.js`, the LiPi Sans woff2 files and the logos are compiled
into the assembly and served from the process, so there is no `wwwroot`
to deploy and no third-party request from any page. Icons come from
`LiPicons.Blazor` (imagiQa's own package, restored from `localpackages/`);
it is the only package reference outside the ASP.NET shared framework.
| `Anjal.Api` | HTTP/JSON API on `HttpListener` + `System.Text.Json` | none |
| `Anjal.Server` | Composition root host process | none |

## Build

    dotnet build

## Test

    dotnet test

## Run the demos

Nine in-process demos prove each pipeline with no external setup:

    dotnet run --project examples/Anjal.InboundEndToEnd
    dotnet run --project examples/Anjal.MailboxEndToEnd
    dotnet run --project examples/Anjal.WebmailDemo       # then open http://127.0.0.1:8080/
    dotnet run --project examples/Anjal.OutboundEndToEnd
    dotnet run --project examples/Anjal.ApiClient
    dotnet run --project examples/Anjal.TlsEndToEnd
    dotnet run --project examples/Anjal.DkimSigning
    dotnet run --project examples/Anjal.InboundAuthCheck
    dotnet run --project examples/Anjal.SubmissionAuth

`Anjal.SubmissionAuth` exercises four scenarios on a submission listener:
1. Correct credentials → AUTH 235 success, message accepted.
2. Wrong password → 535 5.7.8 Authentication credentials invalid.
3. MAIL FROM without AUTH → 530 5.7.0 Authentication required.
4. From-domain not in allowed list → 550 5.7.1 Not authorized.

## Run a real server

Set up the schema once:

    psql -d anjal -f tools/sql/schema.sql

### Single-port (MTA only) - receive mail for local domains

    $env:ANJAL_POSTGRES               = "Host=localhost;Database=anjal;Username=postgres;Password=YOUR-PASSWORD"
    $env:ANJAL_HOSTNAME               = "mail.your-domain.example"
    $env:ANJAL_PORT                   = "25"
    $env:ANJAL_TLS_CERT_PATH          = "/etc/letsencrypt/live/mail.your-domain.example/fullchain.pem"
    $env:ANJAL_TLS_KEY_PATH           = "/etc/letsencrypt/live/mail.your-domain.example/privkey.pem"
    $env:ANJAL_INBOUND_AUTH_ENFORCE   = "dmarc-reject"
    $env:ANJAL_LOCAL_DOMAINS          = "your-domain.example,clinic-a.your-domain.example"
    $env:ANJAL_API_PORT               = "8080"
    $env:ANJAL_API_TOKEN              = "your-strong-secret-token-here"
    dotnet run --project src/Anjal.Server

### Dual-port (MTA + submission) - receive AND let clients send

    $env:ANJAL_POSTGRES               = "Host=localhost;Database=anjal;Username=postgres;Password=YOUR-PASSWORD"
    $env:ANJAL_HOSTNAME               = "mail.your-domain.example"
    $env:ANJAL_PORT                   = "25"
    $env:ANJAL_SUBMISSION_PORT        = "587"
    $env:ANJAL_TLS_CERT_PATH          = "/etc/letsencrypt/live/mail.your-domain.example/fullchain.pem"
    $env:ANJAL_TLS_KEY_PATH           = "/etc/letsencrypt/live/mail.your-domain.example/privkey.pem"
    $env:ANJAL_LOCAL_DOMAINS          = "your-domain.example"
    $env:ANJAL_SUBMISSION_USER        = "lipi-bootstrap"
    $env:ANJAL_SUBMISSION_PASSWORD    = "use-a-strong-password-here"
    $env:ANJAL_SUBMISSION_DOMAINS     = "your-domain.example"
    $env:ANJAL_DKIM_MODE              = "required"
    $env:ANJAL_DKIM_DOMAIN            = "your-domain.example"
    $env:ANJAL_DKIM_SELECTOR          = "default"
    $env:ANJAL_DKIM_KEY_PATH          = "/etc/anjal/dkim/default.private.pem"
    $env:ANJAL_INBOUND_AUTH_ENFORCE   = "dmarc-reject"
    $env:ANJAL_API_PORT               = "8080"
    $env:ANJAL_API_TOKEN              = "your-strong-secret-token-here"
    dotnet run --project src/Anjal.Server

The env-var user is a bootstrap credential - good for one client (e.g.
the local Lipi instance). Add more users via the API as needed.

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `ANJAL_BIND` | `127.0.0.1` | SMTP bind address |
| `ANJAL_PORT` | `2525` | MTA port (inbound from other mail servers) |
| `ANJAL_SUBMISSION_PORT` | (disabled) | Submission port for authenticated clients (e.g. 587) |
| `ANJAL_HOSTNAME` | `anjal.localhost` | Hostname in banner, EHLO, and `Authentication-Results` |
| `ANJAL_POSTGRES` | (in-memory) | PostgreSQL connection string |
| `ANJAL_OUTBOUND_MODE` | `none` | `direct`, `relay`, or `none` |
| `ANJAL_RELAY_HOST` | - | Relay host (when mode=relay) |
| `ANJAL_RELAY_PORT` | `587` | Relay port (when mode=relay) |
| `ANJAL_API_PORT` | (disabled) | HTTP API port |
| `ANJAL_API_BIND` | `ANJAL_BIND` | HTTP API bind address |
| `ANJAL_API_TOKEN` | - | Bearer token for the API (24 characters or more) |
| `ANJAL_API_TOKEN_PREVIOUS` | - | The token being replaced, still accepted until every caller uses the new one (rotation, DEPLOY.md) |
| `ANJAL_TLS_CERT_PATH` | (disabled) | Path to fullchain.pem |
| `ANJAL_TLS_KEY_PATH` | - | Path to privkey.pem (if not in fullchain) |
| `ANJAL_TLS_REQUIRE` | `false` | MTA port requires STARTTLS before MAIL |
| `ANJAL_TLS_DEFAULT_MODE` | `opportunistic` | Default outbound TLS mode |
| `ANJAL_TLS_VALIDATE_PEER` | `true` | Validate remote server certificates |
| `ANJAL_DKIM_MODE` | `off` | `required`, `opportunistic`, or `off` |
| `ANJAL_DKIM_DOMAIN` | - | Default sender domain (for env-var key) |
| `ANJAL_DKIM_SELECTOR` | - | Default selector (for env-var key) |
| `ANJAL_DKIM_KEY_PATH` | - | Path to default DKIM private PEM |
| `ANJAL_INBOUND_AUTH_ENFORCE` | `dmarc-reject` | `dmarc-reject` enforces DMARC; `none`/`off` annotates only |
| `ANJAL_LOCAL_DOMAINS` | (empty) | Comma-separated list of local domains. When set, MTA port refuses RCPT TO for non-local destinations. |
| `ANJAL_SUBMISSION_USER` | - | Env-var bootstrap username for submission AUTH |
| `ANJAL_SUBMISSION_PASSWORD` | - | Env-var bootstrap plaintext password (hashed on startup with PBKDF2) |
| `ANJAL_SUBMISSION_DOMAINS` | - | Env-var user's allowed-from domains (comma-separated). Empty = admin authority. |
| `ANJAL_AUTH_ALLOW_PLAINTEXT` | `false` | Allow AUTH on plaintext channels. ONLY for local dev. |
| `ANJAL_MAILDIR_ROOT` | `/var/mail/anjal` (Unix) / `%LOCALAPPDATA%\Anjal\mail` (Windows) | Root directory for tenant Maildirs (shared by `Anjal.Server` and `Anjal.Webmail`) |
| `ANJAL_SPAM_ACTION` | `junk` | `junk` files scored mail in Junk; `reject` refuses with 550 at or above `ANJAL_SPAM_REJECT_THRESHOLD` |
| `ANJAL_SPAM_REJECT_THRESHOLD` | `5` | Score used by reject mode (Junk filing uses the per-tenant threshold) |
| `ANJAL_SPAM_DNS` | `true` | `false` disables the DNS-based rules (sender MX, HELO resolution, reverse DNS) |
| `ANJAL_RATE_CONN_PER_MIN` | `60` | Connections per client IP per minute, both ports (`0` disables) |
| `ANJAL_RATE_MSG_PER_HOUR` | `200` | Unauthenticated messages per client IP per hour on the MTA port |
| `ANJAL_RATE_USER_MSG_PER_HOUR` | `100` | Messages per authenticated user per hour on the submission port |
| `ANJAL_GREYLIST` | `true` | `false` disables greylisting on the MTA port |
| `ANJAL_GREYLIST_DELAY_SECONDS` | `300` | How long a first-seen (network, sender, recipient) triplet is deferred |

### Deployment

See `deploy/DEPLOY.md` for the full runbook. In short: `deploy/publish.ps1`
builds self-contained linux-x64 binaries and a tarball; `install.sh` on
the VM creates the `anjal` user, `/opt/anjal`, `/etc/anjal` (templates
`server.env`, `webmail.env`, `rclone.conf`), `/var/mail/anjal`,
`/var/lib/anjal`, and installs the units `anjal-server`, `anjal-webmail`
and `anjal-backup.timer`. Both services run as `anjal` with systemd
hardening and `CAP_NET_BIND_SERVICE` for the low ports.

### Health and metrics

    GET /healthz            # no auth: {"status":"ok|degraded|down","components":[store, maildir, tls]}; 503 when down
    GET /metrics            # bearer token: Prometheus text format

Counters: `anjal_smtp_{mta,submission}_connections_total`,
`anjal_smtp_messages_{accepted,deferred,rejected}_total`,
`anjal_mailbox_{delivered,junked}_total`, `anjal_mailbox_bytes_stored_total`,
`anjal_quota_refusals_total`, `anjal_greylist_deferred_total`,
`anjal_ratelimit_{connections,messages}_refused_total`,
`anjal_spam_{scored,rejected}_total`. Gauges: `anjal_outbound_pending`,
`anjal_outbound_sending`, `anjal_greylist_entries`, `anjal_uptime_seconds`.

### Categories

A category is a name and a colour slot, always shown together. Tenant
defaults are seeded once per tenant (Clinical, Referrals, Diagnostics,
Billing, Vendors, Circulars - six, so every mailbox keeps two coloured
slots for its own). A mailbox adds its own in Settings; slots are stored,
never derived from the name and never recomputed when a category is
deleted, because a category that changes colour repaints every chart it
has appeared in. Past the eighth slot a category still works and is told
apart by name.

Assign one from the message page or the list's bulk bar. Ticking "also
file future mail from this sender here" writes a rule that applies at
delivery; an exact address beats a domain rule. Deleting a category
leaves its messages in place, simply uncategorised.

### Dashboard

`/dashboard` reports one mailbox over 7, 30, 90 or 365 days: messages
delivered as the single hero figure with the Junk count directly beneath
it (a dashboard that reports only what reached the inbox hides the number
you want when mail seems to have gone missing), sent and received per day
on one shared scale, received by folder and by category, the five people
who wrote most, and attachment counts. Days with no mail are drawn as
zero rather than closed up. No percentage deltas, no gauges, no second
axis. Every number is repeated in a plain table.

### Quota

Each mailbox has a size limit: 1 GiB by default (DES-11 D2), or what
its organisation's storage plan gives - one size for each person, one
shared total, or both (a size each plus a shared reserve) - set by the
operator in the Anjal console; the organisation's administrator may give
some people a smaller limit, never a larger one. Junk and Trash count.
At or above the limit, RCPT TO that mailbox is deferred with `452 4.2.2
Mailbox full`, so the sending server retries for a few days while the
owner frees space; colleagues' mail is refused the same way; the webmail
warns the person (and the administrators) at 80, 90 and 100% and
refuses to send while full. Anjal's own security mail always arrives.
Deleting permanently (Trash → Delete permanently) releases the bytes.
Through the API, `quotaBytes` null or `0` gives the default.

### TLS and ACME

Set these on **both** processes (they must agree on the store directory):

| Variable | Default | Purpose |
|---|---|---|
| `ANJAL_ACME_DOMAINS` | (off) | Comma-separated DNS names for the certificate, e.g. `mail.anjal.co.in`. Empty disables ACME |
| `ANJAL_ACME_EMAIL` | (none) | Account contact for expiry notices |
| `ANJAL_ACME_DIR` | `/var/lib/anjal/acme` (Unix) / `%LOCALAPPDATA%\Anjal\acme` (Windows) | Where `account.key.pem`, `cert.key.pem`, `fullchain.pem`, `meta.json`, `status.json` live |
| `ANJAL_ACME_STAGING` | `false` | `true` uses Let's Encrypt staging (untrusted certs, generous rate limits) - rehearse with this first |
| `ANJAL_ACME_DIRECTORY` | Let's Encrypt production | Any ACME v2 directory URL; overrides the staging flag |
| `ANJAL_ACME_KEY` | `ecdsa-p256` | Certificate key: `ecdsa-p256` or `rsa-2048` |
| `ANJAL_ACME_RENEW_DAYS` | `30` | Renew when fewer days remain |
| `ANJAL_ACME_HOST` | webmail `true`, server `false` | Which process runs the renewal service. Exactly one per machine |
| `ANJAL_ACME_HTTP_BIND` / `ANJAL_ACME_HTTP_PORT` | `+` / `80` | HTTP-01 responder when the **server** hosts renewal (no webmail deployed) |

Behaviour:

- `Anjal.Server` uses the ACME certificate for STARTTLS on 25/587 when
  `ANJAL_TLS_CERT_PATH` is not set. Until the first certificate exists,
  STARTTLS is simply not advertised.
- `Anjal.Webmail` with `ANJAL_WEBMAIL_HTTPS_PORT=443` serves HTTPS with
  the ACME certificate, answers `/.well-known/acme-challenge/*` on its
  HTTP listener, redirects everything else on HTTP to HTTPS once a
  certificate exists, and sends HSTS. Before the first certificate it
  serves plain HTTP so a DNS mistake cannot lock you out.
- Renewal runs in the webmail by default. A deployment without the
  webmail (transactional only) sets `ANJAL_ACME_HOST=true` on the server,
  which then runs a minimal HTTP-01 responder on port 80.
- Both processes poll the store every 30 s and hot-swap the certificate
  for new connections; nothing is restarted.

    GET    /api/acme                          # certificate + renewal status (reads status.json)
    POST   /api/acme/renew                    # request an immediate renewal (marker file)

    dotnet Anjal.Webmail.dll --acme-renew-now # same, from the command line
    dotnet Anjal.Server.dll  --acme-renew-now

Rehearsal sequence for a new deployment: `ANJAL_ACME_STAGING=true` ->
confirm `GET /api/acme` shows a certificate -> `POST /api/acme/renew` and
confirm a second issuance -> unset staging, delete the store directory
(the staging account and certificate are not usable in production),
restart -> confirm the production certificate -> force one more renewal.
Only then point real clients at it.

### Webmail

Routes: `/sign-in`, `/folder/{name}`, `/message/{id}`, `/compose`,
`/draft/{id}`, `/search`, `/settings`. Every action is a link or a form
POST that redirects, so a refresh never repeats it and every page is
linkable. Six enhancements attach on top when JavaScript is present -
draft autosave, address suggestions, connectivity light, mark-all-read
without a reload, keyboard shortcuts (`j` `k` `Enter` `r` `#`), and a
client-side address check - and each has a working fallback; the
connectivity light and the shortcut hint are simply not rendered
without the script rather than claiming something untrue.

Themes (`paper`, `ink`, `postcard`, `midnight`) are stored per mailbox,
not per browser, so a user's choice follows them to any machine.

### Webmail environment variables (`Anjal.Webmail` process)

| Variable | Default | Purpose |
|---|---|---|
| `ANJAL_WEBMAIL_BIND` | `127.0.0.1` | HTTP bind address |
| `ANJAL_WEBMAIL_PORT` | `8080` | HTTP port |
| `ANJAL_WEBMAIL_HTTPS_PORT` | `0` (off) | HTTPS port, `443` in production; needs ACME or `ANJAL_TLS_CERT_PATH` |
| `ANJAL_WEBMAIL_SECURE` | `false` | `true` marks the session cookie Secure; implied when HTTPS is on |
| `ANJAL_POSTGRES` | (in-memory) | Must point at the same database as `Anjal.Server` |
| `ANJAL_MAILDIR_ROOT` | platform default | Must point at the same directory as `Anjal.Server`; both processes need read/write access |
| `ANJAL_HOSTNAME` | `anjal.localhost` | Used in generated Message-IDs |

Run it alongside the mail server:

    $env:ANJAL_POSTGRES     = "Host=localhost;Database=anjal;Username=postgres;Password=..."
    $env:ANJAL_MAILDIR_ROOT = "C:\anjal\mail"
    dotnet run --project src/Anjal.Webmail

Sign in with a mailbox address and its password (created via
`POST /api/mailboxes`). The webmail talks to the store and Maildir
directly - it does not go through the HTTP API and needs no API token.

### Further settings

Every `ANJAL_*` setting the code reads is described in this README; a test holds it. These are the ones not described above. **`ANJAL_OPERATORS` is needed in production**: without it nobody can open the Anjal console and the service summary goes to nobody. Settings other than secrets (`ANJAL_POSTGRES`, and anything containing PASSWORD, TOKEN, SECRET or KEK) can also be kept in the database with the admin API, so that they are in the backups.

| Variable | Read by | Default | Values | Purpose |
|---|---|---|---|---|
| `ANJAL_DKIM_SEAL_KEY` | Server | `/var/lib/anjal/dkim-seal.pem` on Linux, `%LOCALAPPDATA%\Anjal\dkim-seal.pem` elsewhere | File path | The mail server's seal key (rc.15, DES-11 S6), made at the first start. Its public half is published in the database; the webmail locks every DKIM key it makes with it, and only the mail server can open them, to sign. Keys kept unlocked (or under `ANJAL_KEK`) are locked with it at start. `backup.sh` copies it to the encrypted backup. Losing it unbacked means new DKIM keys and DNS records for every domain. |
| `ANJAL_ALLOW_PLAINTEXT_KEYS` | Both | false | `true` (case-insensitive) or anything else | When ANJAL_KEK is unset and the store is PostgreSQL, DKIM private keys are refused (API returns 409 `kek_required`; Webmail says it cannot make keys). `true` allows storing them unencrypted anyway. Has no effect when ANJAL_KEK is set or the store is in-memory. |
| `ANJAL_API_ALLOW_NO_AUTH` | Server | false | `true` or anything else | Lets the admin API start with no ANJAL_API_TOKEN. Without it, a missing token stops the server (exit 1). Meant for local development only. Only checked when ANJAL_API_PORT > 0. |
| `ANJAL_API_ALLOW_PUBLIC` | Server | false | `true` or anything else | Lets ANJAL_API_BIND be a non-loopback address. Without it, a non-loopback bind stops the server (exit 1), because the API is plain HTTP. Only checked when ANJAL_API_PORT > 0. |
| `ANJAL_BACKUP_SCRATCH` | Webmail | `/var/lib/anjal/backup` (also when set to empty) | Folder path | Folder where backup copies wait before they are sent off. Webmail only measures its size for the operator capacity view; backup.sh is what actually writes there. |
| `ANJAL_BACKUP_RETRY_WAITS` | Backup | `120 240 480 960` (set to empty: no new tries) | Seconds, separated by spaces | How long backup.sh waits before each new try after a failed run (D-88): by default 2, 4, 8 and 16 minutes, about 30 minutes in all. When the last try fails, a `failed` line is added to the status file. "Not configured" is never tried again. |
| `ANJAL_BACKUP_STATUS` | Both | `/var/lib/anjal/backup-status` (but set to empty, it is used as "" and so reads as missing) | File path. Expected contents: a line `ok <timestamp>`, and a line `failed <timestamp> ...` when a run failed since | Status file written by backup.sh. Webmail reads it for the "Last good backup" health tile: over 26 h shows warn; over 48 h, missing, or a run failed after its 30 minutes of tries (D-88) shows bad at once, and the operators are mailed within 15 minutes. |
| `ANJAL_DNS_CHECK` | Webmail | On (the check runs) | `off` (case-insensitive) turns it off. Any other value leaves it on | Daily check of every organisation's DNS records (MX, SPF, DKIM, DMARC, MTA-STS, TLS-RPT). |
| `ANJAL_EVIDENCE_ROOT` | Both | `EvidenceVault.DefaultRoot`: `/var/lib/anjal/evidence` on any non-Windows OS, `%LOCALAPPDATA%\Anjal\evidence` on … | Folder path | Evidence store: SHA-256-fingerprinted originals of every message received or sent, plus a daily manifest chain. Server records always (when the store supports evidence) and passes the path to the admin API. Webmail records its sends only if the folder already … |
| `ANJAL_GEO_FILE` | Webmail | `/var/lib/anjal/ip-locations.bin` on Linux, otherwise `%LOCALAPPDATA%/Anjal/ip-locations.bin`. An empty value also … | File path | Where the packed DB-IP "IP to City Lite" list is kept. It is used to show where sign-ins come from. |
| `ANJAL_GEO_MODE` | Webmail | `download` on Linux, `off` on any other OS. Unknown values also get this default | `download` (fetch the list monthly), `file` (use a list placed by hand, never download), `off`. Trimmed, case-insensitive | Controls the sign-in IP-location lookup and whether its list is downloaded. Lookups are always local, so no address leaves the server. |
| `ANJAL_GREYLIST_REMEMBER_DAYS` | Server | 35 | Integer days. An unparsable value gives 35. No range check | How long a sender that passed greylisting is remembered, so it is not delayed again. Only used when greylisting is on (ANJAL_GREYLIST is not `false`). |
| `ANJAL_GREYLIST_SKIP_SPF_PASS` | Server | true (the exemption is on) | `false` (case-insensitive) turns it off. Anything else leaves it on | A sender whose connecting IP passes SPF for the MAIL FROM domain is not greylisted. Results are cached for 10 minutes per (IP, domain). |
| `ANJAL_GREYLIST_STATE` | Server | `/var/lib/anjal/greylist.tsv` if the folder `/var/lib/anjal` exists (on any OS), otherwise none (memory only). Empty or … | File path, or `none` (case-insensitive) for memory only | Mirror file for remembered greylist senders. The database stays the primary record (log text at 934). |
| `ANJAL_MTA_STS_DOMAINS` | Webmail | The parent domain of ANJAL_HOSTNAME (text after the first dot), or the host name itself if it has no dot | Comma-separated domains, trimmed | Domains whose `mta-sts.<domain>` policy host this webmail serves. Only read when ANJAL_MTA_STS_MODE turns MTA-STS on. |
| `ANJAL_MTA_STS_MAX_AGE` | Webmail | 604800 (1 week) when mode is `enforce`; 86400 (1 day) for `testing` or `none` | Integer seconds, clamped to 60..31557600. Parsed with `NumberStyles.None`, so a sign or spaces make it unparsable and the default is used | `max_age` in the served MTA-STS policy. |
| `ANJAL_MTA_STS_MODE` | Webmail | off in MtaStsPolicy: no policy is served. Inconsistency: the operator health tile treats unset as `testing` and shows … | `off`, `testing`, `enforce`, `none` (trimmed, case-insensitive in MtaStsPolicy). Any other value means off, with a log note | Mode of the MTA-STS policy (RFC 8461) served at `https://mta-sts.<domain>/.well-known/mta-sts.txt`. |
| `ANJAL_MTA_STS_MX` | Webmail | ANJAL_HOSTNAME | Comma-separated MX host names, trimmed | `mx:` lines in the served MTA-STS policy. |
| `ANJAL_OPERATORS` | Webmail | Empty, so nobody is an operator | Comma-separated email addresses, trimmed, compared case-insensitively | People allowed to open the Anjal operator console (`/ops`). They manage the service, not anyone's mail. The service summary mail is also sent to each of them. |
| `ANJAL_POSTMASTER` | Server | Unset: `postmaster@` and `abuse@` at the server's own host name go to `postmaster@<parent domain of ANJAL_HOSTNAME>`, … | Email address (trimmed; empty or whitespace is treated as unset) | The operator's mailbox for `postmaster@` and `abuse@` at the server's own host name. It is ignored (resolves to nothing) if it points at a role address on that same host, to avoid a loop. |
| `ANJAL_PWNED_FILE` | Both | `/var/lib/anjal/pwned-passwords.bin` on Linux, otherwise `%LOCALAPPDATA%/Anjal/pwned-passwords.bin`. Empty also gives … | File path | The leaked-password list file. Server downloads and refreshes it. Both processes check passwords against it and pick up a replaced file within about a minute. |
| `ANJAL_PWNED_MIN_COUNT` | Server | 3 | Integer ≥ 1. A value < 1 or unparsable gives 3 | When building the downloaded list, a password is kept only if it appears in at least this many leaks. |
| `ANJAL_PWNED_MODE` | Both | `both` | `both`, `download` (nothing is sent out when a password is set), `online`, `off`. Trimmed, case-insensitive; unknown values mean `both` | Which leaked-password checks run: the local downloaded list, the online HIBP k-anonymity range check, both, or neither. |
| `ANJAL_PWNED_ONLINE_MIN_COUNT` | Both | 1 (any leak) | Integer ≥ 1. A value < 1 or unparsable gives 1 | Smallest leak count that makes the online check refuse a password. |
| `ANJAL_PWNED_REFRESH_DAYS` | Server | 182 | Integer days. ≤ 0 turns refreshing off | Re-downloads the list when it is at least this old (checked every 6 hours, first check 5 minutes after start; needs about 8 GB free). Not used when the mode doesn't use the download or the source is `off`. |
| `ANJAL_PWNED_SOURCE` | Server | Download is on, from the HIBP range API (the hard-coded `https://api.pwnedpasswords.com/range/`) | Only `off` (case-insensitive) is recognised. Any other value is ignored; it is not a URL or path | `off` stops the server downloading or refreshing the list. A file placed by hand is still used. |
| `ANJAL_SMTP_AUTH_FAILURES_PER_IP` | Server | 10 | Integer ≥ 1. A value < 1 makes `AuthFailureLimiter` throw `ArgumentOutOfRangeException` at startup (no try/catch, so the server fails to … | Failed logins allowed from one IP in a 15-minute sliding window before that IP is refused before any password check. Shared by ports 587 and 465. Only read when ANJAL_SUBMISSION_PORT > 0. |
| `ANJAL_SMTP_IDLE_TIMEOUT_SECONDS` | Server | 300 | Integer seconds. No range check: unclear what ≤ 0 does, because it goes straight into `CancelAfter` (SmtpSession.cs:1265) | Per-command idle timeout, also used as the TLS handshake timeout. Applies to all SMTP listeners (25/587/465). |
| `ANJAL_SMTP_MAX_CONNECTIONS` | Server | 200 | Integer. No range check (0 or less would refuse every connection, SmtpServer.cs:191) | Maximum concurrent SMTP sessions per listener (the 587/465 listeners copy the port-25 value). |
| `ANJAL_SMTP_MAX_CONNECTIONS_PER_IP` | Server | 10 | Integer. No range check | Maximum concurrent SMTP sessions from one IP address. |
| `ANJAL_SMTP_MAX_SESSION_MINUTES` | Server | 15 | Integer minutes. No range check (unclear for ≤ 0: `CancelAfter`, SmtpSession.cs:129) | Hard cap on one SMTP session's total length, however active it is. Stops slow-drip clients. |
| `ANJAL_SUBMISSION_TLS_PORT` | Server | 0 (disabled) | Integer port > 0. Empty, unparsable or ≤ 0 gives 0 | Port for implicit-TLS submission (RFC 8314, normally 465), with the same auth, limits and policy as 587. Only read inside the ANJAL_SUBMISSION_PORT > 0 block, so it does nothing unless ANJAL_SUBMISSION_PORT is also set. |
| `ANJAL_TLS_ALLOW_PLAINTEXT` | Server | false. Outbound mail is never sent unencrypted: if TLS fails, the message is held, retried and finally bounced | `true` or anything else | `true` allows outbound SMTP to fall back to plaintext when TLS can't be used. For test rigs only. Not related to inbound AUTH (that is ANJAL_AUTH_ALLOW_PLAINTEXT). |
| `ANJAL_TLS_REVOCATION` | Server | `online` (CRL/OCSP checked; soft-fails only when status can't be obtained) | `nocheck` (case-insensitive) skips revocation. Any other value means online | Certificate revocation checking when validating the receiving server's certificate on outbound delivery. |
| `ANJAL_WEBHOOK_ALLOW_HTTP` | Server | false (https only) | `true` or anything else | Allows plain `http://` webhook URLs, for a receiver on a private network. |
| `ANJAL_WEBHOOK_ALLOW_PRIVATE` | Server | false | `true` or anything else | Allows webhook targets on loopback or private addresses (RFC 1918, RFC 4193, link-local). This is checked both when a rule is saved and on the address actually connected to (anti-SSRF and anti-DNS-rebinding). Needed when SIGMA or Lipi run on the same host or … |
| `ANJAL_WEBMAIL_HTTP3` | Webmail | On (if QUIC is available) | `false` (case-insensitive) turns it off. Anything else means on | Offers HTTP/3 (QUIC on the HTTPS port over UDP). Only matters when HTTPS is on (ANJAL_WEBMAIL_HTTPS_PORT > 0). If QUIC is missing (Linux needs libmsquic and IPv6), it falls back to HTTP/1.1 and HTTP/2 rather than failing. |

## SMTP submission authentication

Anjal's submission listener (port 587 by convention) accepts mail only
from clients that authenticate via SMTP `AUTH PLAIN` or `AUTH LOGIN`
(RFC 4954). Authenticated clients can send mail with any destination
(relay), but only from sender domains they're authorized for.

### Security model

- **Strict TLS-before-AUTH.** The submission port advertises and accepts
  AUTH only after STARTTLS. AUTH on a plaintext channel is refused with
  `538 5.7.11 Encryption required`. For local development, set
  `ANJAL_AUTH_ALLOW_PLAINTEXT=true`.
- **Strict envelope-domain authorization.** A user with
  `allowedFromDomains = ["clinic-a.com"]` who tries
  `MAIL FROM:<x@evil.com>` gets `550 5.7.1 Not authorized to send as
  evil.com`. Empty `allowedFromDomains` means admin authority (any
  domain).
- **Open-relay guard on port 25.** When `ANJAL_LOCAL_DOMAINS` is set (or
  the `local_domains` table has rows), the MTA listener refuses RCPT TO
  for non-local destinations with `550 5.7.1 Relaying denied`. This is
  what prevents Anjal from being used by spammers.
- **PBKDF2-SHA256 password storage.** Plaintext passwords are hashed
  with 100,000 iterations and a 128-bit salt before persisting. The
  stored hash format is `pbkdf2$iterations$salt-b64$hash-b64`. The
  Anjal.Api layer never returns hashes in responses.

### Manage users via the API

Add a submission user (the password is plaintext on the wire, hashed
by Anjal before persisting; use HTTPS in production):

    POST /api/smtp-users
    Authorization: Bearer <ANJAL_API_TOKEN>
    Content-Type: application/json

    {
      "username": "lipi-tenant-A",
      "password": "use-a-strong-password",
      "allowedFromDomains": ["clinic-a.com", "*.clinic-a.com"],
      "enabled": true
    }

List users (hashes never returned):

    GET /api/smtp-users
    Authorization: Bearer <ANJAL_API_TOKEN>

Remove a user:

    DELETE /api/smtp-users/lipi-tenant-A
    Authorization: Bearer <ANJAL_API_TOKEN>

### Manage local domains via the API

The list of local domains can be managed at runtime via the API in
addition to the `ANJAL_LOCAL_DOMAINS` env var (the env var serves as a
bootstrap default; the API table is checked second):

    POST /api/local-domains
    Authorization: Bearer <ANJAL_API_TOKEN>
    Content-Type: application/json

    {"domain": "clinic-a.com"}

List:

    GET /api/local-domains
    Authorization: Bearer <ANJAL_API_TOKEN>

Remove:

    DELETE /api/local-domains/clinic-a.com
    Authorization: Bearer <ANJAL_API_TOKEN>

## Inbound authentication (SPF + DKIM + DMARC)

For every inbound message on port 25, Anjal runs three checks in
parallel:

- **SPF (RFC 7208)**: looks up the SPF TXT record for the MAIL FROM
  domain, walks any `include:` chain (10-lookup limit), and matches the
  peer IP against `ip4:`, `ip6:`, `a`, `mx`, and `exists` mechanisms.
- **DKIM (RFC 6376)**: finds the first `DKIM-Signature` header, fetches
  the public key from `<selector>._domainkey.<domain>` TXT, canonicalizes
  the body and signed headers per the algorithm tags, and verifies the
  RSA-SHA256 signature.
- **DMARC (RFC 7489)**: looks up `_dmarc.<from-domain>` TXT (falling back
  to the organizational domain), checks SPF and DKIM alignment with the
  From-header domain, and applies the published policy.

The verdicts are formatted into an RFC 8601 `Authentication-Results`
header prepended to the message, and the full per-verifier detail is
attached to the webhook payload as `authResults`.

### Enforcement

With `ANJAL_INBOUND_AUTH_ENFORCE=dmarc-reject` (the default), Anjal
refuses messages at the SMTP layer (550 reply) when the From-domain
publishes `p=reject` and authentication fails. The message never reaches
the sink or the webhook. Policies `p=quarantine` and `p=none` are
advisory; Anjal annotates them but does not refuse the message.

To disable enforcement entirely, set `ANJAL_INBOUND_AUTH_ENFORCE=none`.

## DKIM setup walkthrough

DKIM signs outbound mail so receivers can verify it genuinely came from
your domain.

### Step 1: Generate a keypair

    dotnet run --project examples/Anjal.DkimKeygen -- mail.your-domain.example default ./keys

### Step 2: Publish the DNS TXT record

At your DNS provider, create a TXT record at:

    default._domainkey.mail.your-domain.example

with the value printed by step 1.

### Step 3: Configure Anjal

Either via env vars (single sender domain) or via the API for
multi-domain setups:

    POST /api/dkim-keys
    Authorization: Bearer <ANJAL_API_TOKEN>

    {
      "domain": "mail.your-domain.example",
      "selector": "default",
      "privateKeyPem": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----\n"
    }

## HTTP API

All endpoints require `Authorization: Bearer <ANJAL_API_TOKEN>` unless
the configured token is empty (test-only mode).

### Routing rules

    POST   /api/routing-rules                 { localPart, webhookUrl, webhookSecret }
    GET    /api/routing-rules
    DELETE /api/routing-rules/{localPart}

### Tag grants

    POST   /api/tag-grants                    { localPart, tag, correlationKey, ttlSeconds }

### Outbound

    POST   /api/outbound                      { envelopeFrom, envelopeTo, subject?, bodyText?, rawBytesBase64?, giveUpHours? }
    GET    /api/outbound/{id}

### Inbound

    GET    /api/inbound/{id}

### Outbound TLS policies

    POST   /api/outbound-tls-policies         { domain, mode }
    GET    /api/outbound-tls-policies
    DELETE /api/outbound-tls-policies/{domain}

### DKIM keys

    POST   /api/dkim-keys                     { domain, selector, privateKeyPem }
    GET    /api/dkim-keys                     # never returns private keys
    DELETE /api/dkim-keys/{domain}

### SMTP submission users (v0.8.0)

    POST   /api/smtp-users                    { username, password, allowedFromDomains, enabled }
    GET    /api/smtp-users                    # never returns password hashes
    DELETE /api/smtp-users/{username}

### Local domains (v0.8.0)

    POST   /api/local-domains                 { domain }
    GET    /api/local-domains
    DELETE /api/local-domains/{domain}

### Anti-spam (v0.11.0)

Scoring runs only on unauthenticated (MTA-port) deliveries. Each rule
that fires adds points; the sum is written to the message and compared
with the tenant's `spamThreshold` (default 5, `0` disables Junk filing).

| Rule | Points | Rule | Points |
|---|---|---|---|
| SPF fail / softfail / none | 3 / 2 / 1 | HELO malformed / unresolvable | 2 / 1 |
| DKIM fail / none | 3 / 1 | No reverse DNS for client IP | 1 |
| DMARC fail | 3 | From header domain != envelope domain | 1 |
| Sender domain has no MX or A | 3 | Missing From / Message-ID / Date | 2 / 1 / 1 |
| Subject all upper-case | 1 | Phrase list hits (capped) | 1 each, max 3 |
| More than 20 recipients | 1 | | |

Sender rules (`alice@example.com` or `@example.com`, matched against the
envelope sender and the From header) override the score: *allow* forces
INBOX, *block* forces Junk. Exact-address rules beat domain rules; block
beats allow. The webmail's *Report spam* adds a block rule and *Not spam*
adds an allow rule for the sender.

Rate limits and greylisting are temporary refusals (4xx), not spam
verdicts: legitimate mail servers retry. Loopback and private-network
clients and authenticated sessions are never greylisted.

    POST   /api/tenants                       { ..., spamThreshold }
    GET    /api/tenants/{slug}/sender-rules
    POST   /api/tenants/{slug}/sender-rules   { pattern, action: "allow" | "block" }
    DELETE /api/tenants/{slug}/sender-rules/{pattern}

### Tenants, domains and mailboxes (v0.9.0)

Tenant creation is admin-API-only. A domain registered here is treated as
verified and becomes RCPT-able on the MTA port immediately (it is
consulted by the local-domain resolver alongside `local_domains` and
`ANJAL_LOCAL_DOMAINS`).

    POST   /api/tenants                       { slug, displayName, enabled }
    GET    /api/tenants
    GET    /api/tenants/{slug}
    DELETE /api/tenants/{slug}                # cascades index rows; Maildir files stay on disk

    POST   /api/tenant-domains                { tenantSlug, domain }
    GET    /api/tenant-domains[?tenant=slug]
    DELETE /api/tenant-domains/{domain}

    POST   /api/mailboxes                     { tenantSlug, address, password, displayName, enabled, quotaBytes }
    GET    /api/mailboxes[?tenant=slug]       # never returns password hashes
    GET    /api/mailboxes/{address}
    DELETE /api/mailboxes/{address}
    GET    /api/mailboxes/{address}/folders
    GET    /api/mailboxes/{address}/messages[?folder=INBOX&limit=50&offset=0]

    GET    /api/messages/{id}                 # metadata
    GET    /api/messages/{id}/raw             # { id, rawBytesBase64 }

Creating a mailbox lays out `<root>/<tenant-slug>/<local@domain>/` with
`tmp/`, `new/`, `cur/` and the Maildir++ folders `.Sent`, `.Drafts`,
`.Junk`, `.Trash`. An empty password makes the mailbox receive-only; on update, an
omitted password keeps the existing one. The default size is 1 GiB and
is enforced (see Quota).

A mailbox authenticates on the submission port with its full address as
the username and is allowed to send `MAIL FROM` its own domain only.
`smtp_users` remain the way to create service accounts with broader
from-domain authority.

## Regenerate API docs

    python tools/gen_api_docs.py

## Line endings

Every tracked file has an explicit rule in `.gitattributes` (CRLF for sources, docs and
web files; LF for scripts, workflows, systemd units and env templates; binary for
images, fonts and packages), and `.editorconfig` gives the same ending. Check or fix:

    python tools/check_eol.py          # lists any fault and fails
    python tools/check_eol.py --fix    # rewrites working files to their rule

CI runs the check in the style workflow; `deploy.ps1` runs `--fix` before formatting.
A new file type needs a line in both files.

## Pre-push verification (Windows)

    .\deploy.ps1 -Message "Your commit message"
