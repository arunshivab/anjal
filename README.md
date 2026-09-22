# Anjal (அஞ்சல்)

A .NET 10 mail server library. Protocol code (MIME, SMTP, DNS, DKIM, SPF,
DMARC) and the HTTP API are hand-written with no external NuGet
dependencies. TLS uses the BCL's `System.Net.Security.SslStream`. DKIM
and DMARC signature verification use the BCL's
`System.Security.Cryptography.RSA`. Password hashing uses the BCL's
`Rfc2898DeriveBytes.Pbkdf2`. The PostgreSQL store layer uses
[Npgsql](https://www.npgsql.org/).

## Status

**v0.17.0** - **formatted mail, signatures, and the fixes from QA phase A1
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
| `ANJAL_API_TOKEN` | - | Bearer token for the API |
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

Each mailbox has `quotaBytes` (default 2 GiB, `0` = unlimited). At or
above it, RCPT TO that mailbox is deferred with `452 4.2.2 Mailbox
full`, so the sending server retries for a few days while the owner
frees space; the webmail shows usage in the sidebar and refuses to send
while full. Deleting permanently (Trash → Delete permanently) releases
the bytes.

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
omitted password keeps the existing one. The default quota is 2 GiB and
is soft in this release (exceeding it is logged, not enforced).

A mailbox authenticates on the submission port with its full address as
the username and is allowed to send `MAIL FROM` its own domain only.
`smtp_users` remain the way to create service accounts with broader
from-domain authority.

## Regenerate API docs

    python tools/gen_api_docs.py

## Pre-push verification (Windows)

    .\deploy.ps1 -Message "Your commit message"
