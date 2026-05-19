# Anjal (அஞ்சல்)

A .NET 10 mail server library. Protocol code (MIME, SMTP, DNS) and the
HTTP API are hand-written with no external NuGet dependencies. TLS uses
the BCL's `System.Net.Security.SslStream`. The PostgreSQL store layer
uses [Npgsql](https://www.npgsql.org/).

## Status

**v0.5.0** - bidirectional mail + HTTP/JSON API + STARTTLS. Anjal now
accepts STARTTLS on the receiver side and uses STARTTLS opportunistically
on the sender side, with per-destination policy control. RFC 3207 wire
format. RFC 5246 / 8446 (TLS 1.2 / 1.3) via the BCL.

DKIM signing is the next phase.

## Modules

| Module | Purpose | NuGet |
|---|---|---|
| `Anjal.Mime` | MIME parser and builder (RFC 5322, RFC 2045-2049) | none |
| `Anjal.Smtp` | SMTP receiver and sender (RFC 5321), STARTTLS (RFC 3207) | none |
| `Anjal.Dns` | DNS MX record resolver (RFC 1035) | none |
| `Anjal.Routing` | Inbound routing + signed webhook dispatcher | none |
| `Anjal.Store` | Persistence: in-memory and PostgreSQL | Npgsql |
| `Anjal.Api` | HTTP/JSON API on `HttpListener` + `System.Text.Json` | none |
| `Anjal.Server` | Composition root host process | none |

## Build

    dotnet build

## Test

    dotnet test

## Run the demos

Four end-to-end demos prove inbound, outbound, full-stack HTTP, and
STARTTLS pipelines in-process. No external setup needed - the TLS demo
generates a self-signed cert in-process:

    dotnet run --project examples/Anjal.InboundEndToEnd
    dotnet run --project examples/Anjal.OutboundEndToEnd
    dotnet run --project examples/Anjal.ApiClient
    dotnet run --project examples/Anjal.TlsEndToEnd

## Run a real server

Set up the schema once:

    psql -d anjal -f tools/sql/schema.sql

### Inbound-only, no API, no TLS

    $env:ANJAL_POSTGRES = "Host=localhost;Database=anjal;Username=postgres;Password=YOUR-PASSWORD"
    dotnet run --project src/Anjal.Server

### Full stack with STARTTLS (recommended for production)

    $env:ANJAL_POSTGRES      = "Host=localhost;Database=anjal;Username=postgres;Password=YOUR-PASSWORD"
    $env:ANJAL_HOSTNAME      = "mail.your-domain.example"
    $env:ANJAL_TLS_CERT_PATH = "/etc/letsencrypt/live/mail.your-domain.example/fullchain.pem"
    $env:ANJAL_TLS_KEY_PATH  = "/etc/letsencrypt/live/mail.your-domain.example/privkey.pem"
    $env:ANJAL_OUTBOUND_MODE = "direct"
    $env:ANJAL_API_PORT      = "8080"
    $env:ANJAL_API_TOKEN     = "your-strong-secret-token-here"
    dotnet run --project src/Anjal.Server

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `ANJAL_BIND` | `127.0.0.1` | SMTP bind address |
| `ANJAL_PORT` | `2525` | SMTP receiver port |
| `ANJAL_HOSTNAME` | `anjal.localhost` | Hostname in banner and EHLO |
| `ANJAL_POSTGRES` | (in-memory) | PostgreSQL connection string |
| `ANJAL_OUTBOUND_MODE` | `none` | `direct`, `relay`, or `none` |
| `ANJAL_RELAY_HOST` | - | Relay host (when mode=relay) |
| `ANJAL_RELAY_PORT` | `587` | Relay port (when mode=relay) |
| `ANJAL_API_PORT` | (disabled) | HTTP API port. Unset disables. |
| `ANJAL_API_BIND` | `ANJAL_BIND` | HTTP API bind address |
| `ANJAL_API_TOKEN` | - | Bearer token for the API. Empty disables auth. |
| `ANJAL_TLS_CERT_PATH` | (disabled) | Path to fullchain.pem |
| `ANJAL_TLS_KEY_PATH` | - | Path to privkey.pem (if not in fullchain) |
| `ANJAL_TLS_REQUIRE` | `false` | If `true`, server rejects MAIL FROM until STARTTLS |
| `ANJAL_TLS_DEFAULT_MODE` | `opportunistic` | Default outbound TLS mode if no per-domain policy |
| `ANJAL_TLS_VALIDATE_PEER` | `true` | Validate remote server certificate. Set `false` only for testing |

## TLS overview

**Receiver side.** When `ANJAL_TLS_CERT_PATH` is set, Anjal loads the PEM
cert and key (via `X509Certificate2.CreateFromPemFile`) and advertises
`STARTTLS` in the EHLO response. Clients can upgrade with the `STARTTLS`
command; per RFC 3207 the session state resets and the client must
re-issue EHLO over the encrypted channel. If `ANJAL_TLS_REQUIRE=true`,
the server returns `530 Must issue a STARTTLS command first` to any
`MAIL FROM` issued before STARTTLS.

**Sender side.** When connecting outbound, Anjal:

1. Sends the initial EHLO
2. Looks at the EHLO response for the `STARTTLS` capability
3. Looks up the destination's TLS mode in `outbound_tls_policies`, or
   falls back to `ANJAL_TLS_DEFAULT_MODE`
4. Depending on the mode:
   - **opportunistic** - upgrade if offered, send plaintext if not
   - **required** - upgrade if offered, fail transient if not
   - **disabled** - skip STARTTLS even if offered
5. After successful handshake, re-issue EHLO over TLS, then `MAIL FROM`,
   `RCPT TO`, `DATA`, `QUIT`

The policy lookup is keyed by the **destination domain** (e.g.
`gmail.com`), not by MX hostname. In relay mode the lookup is against
the relay's hostname. This applies uniformly to direct and relay outbound modes.

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

`mode` is one of `opportunistic`, `required`, `disabled`.

## Webhook integration

When a message is accepted for a registered local-part, Anjal POSTs a
signed JSON payload to the registered webhook URL:

- `X-Anjal-Timestamp` - Unix seconds at the time of send
- `X-Anjal-Signature` - `sha256=<hex>` of HMAC-SHA256 over `timestamp + "." + body`

See `examples/Anjal.InboundEndToEnd` for the payload schema.

## Outbound queue

Outbound messages are queued in `outbound_messages` and drained by the
`OutboundWorker` background task. Failed attempts retry with exponential
backoff (1m, 5m, 15m, 1h, 6h, 24h). Messages are marked Failed if they
reach their `give_up_at` deadline (default 24 hours) or receive a 5xx
reply.

## Sub-addressing

A recipient `local-part+tag@host` routes by `local-part`; the `tag` is
exposed in the webhook payload. Tags must be authorised by an active
`TagGrant` for the matching `(localPart, tag)`, otherwise SMTP returns 550.

## Regenerate API docs

    python tools/gen_api_docs.py

## Pre-push verification (Windows)

    .\deploy.ps1 -Message "Your commit message"
