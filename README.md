# Anjal (அஞ்சல்)

A .NET 10 mail server library. Protocol code (MIME, SMTP, DNS, DKIM, SPF,
DMARC) and the HTTP API are hand-written with no external NuGet
dependencies. TLS uses the BCL's `System.Net.Security.SslStream`. DKIM
and DMARC signature verification use the BCL's
`System.Security.Cryptography.RSA`. The PostgreSQL store layer uses
[Npgsql](https://www.npgsql.org/).

## Status

**v0.7.0** - bidirectional mail + HTTP/JSON API + STARTTLS + DKIM signing
**+ SPF/DKIM/DMARC inbound verification**. Anjal now signs every outbound
message and verifies every inbound message. With DMARC enforcement enabled,
messages whose sending domain publishes `p=reject` and which fail
authentication are refused with SMTP 550 before reaching the routing layer.

## Modules

| Module | Purpose | NuGet |
|---|---|---|
| `Anjal.Mime` | MIME parser and builder (RFC 5322, RFC 2045-2049) | none |
| `Anjal.Smtp` | SMTP receiver and sender (RFC 5321), STARTTLS (RFC 3207) | none |
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

Six in-process demos prove each pipeline with no external setup:

    dotnet run --project examples/Anjal.InboundEndToEnd
    dotnet run --project examples/Anjal.OutboundEndToEnd
    dotnet run --project examples/Anjal.ApiClient
    dotnet run --project examples/Anjal.TlsEndToEnd
    dotnet run --project examples/Anjal.DkimSigning
    dotnet run --project examples/Anjal.InboundAuthCheck

`Anjal.InboundAuthCheck` exercises three scenarios:
1. A properly DKIM-signed message verifies successfully.
2. A message with a tampered body produces a body-hash mismatch (DKIM fail).
3. An unsigned message reports no signature.

It prints the formatted `Authentication-Results` header and the JSON
fragment Anjal would attach to the webhook payload.

## Run a real server

Set up the schema once:

    psql -d anjal -f tools/sql/schema.sql

### Full stack with TLS, DKIM signing, and inbound authentication

    $env:ANJAL_POSTGRES               = "Host=localhost;Database=anjal;Username=postgres;Password=YOUR-PASSWORD"
    $env:ANJAL_HOSTNAME               = "mail.your-domain.example"
    $env:ANJAL_TLS_CERT_PATH          = "/etc/letsencrypt/live/mail.your-domain.example/fullchain.pem"
    $env:ANJAL_TLS_KEY_PATH           = "/etc/letsencrypt/live/mail.your-domain.example/privkey.pem"
    $env:ANJAL_OUTBOUND_MODE          = "direct"
    $env:ANJAL_DKIM_MODE              = "required"
    $env:ANJAL_DKIM_DOMAIN            = "mail.your-domain.example"
    $env:ANJAL_DKIM_SELECTOR          = "default"
    $env:ANJAL_DKIM_KEY_PATH          = "/etc/anjal/dkim/default.private.pem"
    $env:ANJAL_INBOUND_AUTH_ENFORCE   = "dmarc-reject"
    $env:ANJAL_API_PORT               = "8080"
    $env:ANJAL_API_TOKEN              = "your-strong-secret-token-here"
    dotnet run --project src/Anjal.Server

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `ANJAL_BIND` | `127.0.0.1` | SMTP bind address |
| `ANJAL_PORT` | `2525` | SMTP receiver port |
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
| `ANJAL_TLS_REQUIRE` | `false` | Server requires STARTTLS before MAIL |
| `ANJAL_TLS_DEFAULT_MODE` | `opportunistic` | Default outbound TLS mode |
| `ANJAL_TLS_VALIDATE_PEER` | `true` | Validate remote server certificates |
| `ANJAL_DKIM_MODE` | `off` | `required`, `opportunistic`, or `off` |
| `ANJAL_DKIM_DOMAIN` | - | Default sender domain (for env-var key) |
| `ANJAL_DKIM_SELECTOR` | - | Default selector (for env-var key) |
| `ANJAL_DKIM_KEY_PATH` | - | Path to default DKIM private PEM |
| `ANJAL_INBOUND_AUTH_ENFORCE` | `dmarc-reject` | `dmarc-reject` enforces DMARC; `none`/`off` annotates only |

## Inbound authentication (SPF + DKIM + DMARC)

For every inbound message, Anjal runs three checks in parallel:

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
Authentication still runs and the verdicts are still attached to the
webhook payload, but messages are always accepted.

### Webhook payload schema (with auth)

```json
{
  "inboundMessageId": "...",
  "recipient": "...",
  "envelopeFrom": "...",
  "subject": "...",
  "messageId": "...",
  "receivedAt": "2026-05-19T12:00:00.0000000+00:00",
  "rawBytesBase64": "...",
  "authResults": {
    "spf":   { "result": "pass",  "domain": "...", "peerAddress": "...", "matchedMechanism": "ip4:...", "lookupCount": 2, "explanation": "..." },
    "dkim":  { "result": "pass",  "domain": "...", "selector": "...",    "algorithm": "rsa-sha256",     "explanation": "..." },
    "dmarc": { "result": "pass",  "fromDomain": "...", "policy": "reject", "spfAlignment": "relaxed",
               "dkimAlignment": "relaxed", "spfAligned": true, "dkimAligned": true, "alignedDomain": "...",
               "explanation": "..." },
    "headerValue": "<the full Authentication-Results header line>"
  }
}
```

When authentication is disabled, the `authResults` key is omitted from
the payload.

## DKIM setup walkthrough

DKIM signs outbound mail so receivers can verify it genuinely came from
your domain. Gmail and Outlook strongly prefer signed mail; an unsigned
message has a much higher chance of landing in spam.

### Step 1: Generate a keypair

    dotnet run --project examples/Anjal.DkimKeygen -- mail.your-domain.example default ./keys

This writes `default.private.pem` and `default.public.pem` and prints
the DNS TXT record to publish, of the form:

    v=DKIM1; k=rsa; p=MIIBIjANBgkqhkiG9w0BAQEFAAO...

### Step 2: Publish the DNS TXT record

At your DNS provider, create a TXT record at:

    default._domainkey.mail.your-domain.example

with the value printed by step 1. Wait for DNS propagation (usually a
few minutes; verify with `dig TXT default._domainkey.mail.your-domain.example`).

### Step 3: Configure Anjal

Either via env vars (single sender domain):

    $env:ANJAL_DKIM_MODE     = "required"
    $env:ANJAL_DKIM_DOMAIN   = "mail.your-domain.example"
    $env:ANJAL_DKIM_SELECTOR = "default"
    $env:ANJAL_DKIM_KEY_PATH = "./keys/default.private.pem"

Or upload via API (one or more sender domains, with the env-var key as
fallback default):

    POST /api/dkim-keys
    Authorization: Bearer <ANJAL_API_TOKEN>
    Content-Type: application/json

    {
      "domain": "mail.your-domain.example",
      "selector": "default",
      "privateKeyPem": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----\n"
    }

### Step 4: Verify

Send mail to any address you control. The receiving server's headers
will show `Authentication-Results: ... dkim=pass`. Tools like
[mail-tester.com](https://www.mail-tester.com/) report a clear DKIM
pass/fail.

### Key rotation

To rotate, repeat steps 1-3 with a new selector (e.g. `2026a`):

    POST /api/dkim-keys
    { "domain": "mail.your-domain.example", "selector": "2026a", "privateKeyPem": "..." }

Mail signed with the old selector continues to verify until you remove
the old DNS TXT record.

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

## TLS overview

**Receiver side.** When `ANJAL_TLS_CERT_PATH` is set, Anjal advertises
`STARTTLS` in EHLO and accepts upgrades. Per RFC 3207, the session
state resets after STARTTLS and clients must re-issue EHLO. With
`ANJAL_TLS_REQUIRE=true`, plain-text `MAIL FROM` is refused with
`530 Must issue a STARTTLS command first`.

**Sender side.** Outbound TLS is per-destination-domain. Policy entries
in `outbound_tls_policies` (managed via the API) override the default
`ANJAL_TLS_DEFAULT_MODE`. Modes:

- `opportunistic` - try STARTTLS if offered, fall back to plaintext
- `required` - fail transient if STARTTLS is not offered
- `disabled` - skip STARTTLS even if offered

## DKIM signing overview

Every outbound message has its `From:` header parsed for the sender
domain. The signer looks up the DKIM key for that domain (env-var key
first, then the `dkim_keys` store), signs the message with RSA-SHA256,
and prepends a `DKIM-Signature:` header before handing to the SMTP
sender. With `ANJAL_DKIM_MODE=required`, sends fail permanently if no
key is found for the sender domain.

Canonicalization: `relaxed/relaxed` by default (RFC 6376 section 3.4).
Signed headers default to: From, To, Subject, Date, Message-ID,
MIME-Version, Content-Type. The `From` header is always included even
if explicitly excluded from the list (required by the RFC).

## Webhook integration

When a message is accepted for a registered local-part, Anjal POSTs a
signed JSON payload to the registered webhook URL:

- `X-Anjal-Timestamp` - Unix seconds at the time of send
- `X-Anjal-Signature` - `sha256=<hex>` of HMAC-SHA256 over `timestamp + "." + body`

See `examples/Anjal.InboundEndToEnd` for the payload schema. When
inbound authentication is enabled, the payload includes an `authResults`
block (schema above).

## Outbound queue

Outbound messages are queued in `outbound_messages` and drained by the
`OutboundWorker` background task. Failed attempts retry with exponential
backoff (1m, 5m, 15m, 1h, 6h, 24h). Messages are marked Failed if they
reach their `give_up_at` deadline (default 24 hours) or receive a 5xx
reply.

## Sub-addressing

A recipient `local-part+tag@host` routes by `local-part`; the `tag` is
exposed in the webhook payload. Tags must be authorised by an active
`TagGrant` for the matching `(localPart, tag)`.

## Regenerate API docs

    python tools/gen_api_docs.py

## Pre-push verification (Windows)

    .\deploy.ps1 -Message "Your commit message"
