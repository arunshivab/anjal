# Anjal (அஞ்சல்)

A .NET 10 mail server library. Protocol code (MIME, SMTP, DNS, DKIM, SPF,
DMARC) and the HTTP API are hand-written with no external NuGet
dependencies. TLS uses the BCL's `System.Net.Security.SslStream`. DKIM
and DMARC signature verification use the BCL's
`System.Security.Cryptography.RSA`. Password hashing uses the BCL's
`Rfc2898DeriveBytes.Pbkdf2`. The PostgreSQL store layer uses
[Npgsql](https://www.npgsql.org/).

## Status

**v0.8.0** - full SMTP server with bidirectional mail + DKIM signing +
SPF/DKIM/DMARC inbound verification + **SMTP submission authentication
on dual-port (25/587) with open-relay guard**. Anjal can now be safely
exposed to the internet: port 25 accepts inbound from other servers only
for local domains, port 587 requires authenticated submission from
clients with per-user from-domain authorization.

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
| `Anjal.Api` | HTTP/JSON API on `HttpListener` + `System.Text.Json` | none |
| `Anjal.Server` | Composition root host process | none |

## Build

    dotnet build

## Test

    dotnet test

## Run the demos

Seven in-process demos prove each pipeline with no external setup:

    dotnet run --project examples/Anjal.InboundEndToEnd
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

## Regenerate API docs

    python tools/gen_api_docs.py

## Pre-push verification (Windows)

    .\deploy.ps1 -Message "Your commit message"
