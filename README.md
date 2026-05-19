# Anjal (அஞ்சல்)

A .NET 10 mail server library. Protocol code (MIME, SMTP, DNS) is
hand-written with no NuGet dependencies. The PostgreSQL store layer
uses [Npgsql](https://www.npgsql.org/).

## Status

**v0.3.0** - bidirectional. Inbound: SMTP receiver → MIME parse → routing
table → HMAC-signed webhook. Outbound: queue → DNS-resolved direct delivery
or single-relay submission, with exponential-backoff retries.

No TLS, DKIM, SPF, or DMARC yet - that's Phase 2.

## Modules

| Module | Purpose | NuGet |
|---|---|---|
| `Anjal.Mime` | MIME parser and builder (RFC 5322, RFC 2045-2049) | none |
| `Anjal.Smtp` | SMTP receiver and sender (RFC 5321) | none |
| `Anjal.Dns` | DNS MX record resolver (RFC 1035) | none |
| `Anjal.Routing` | Inbound routing + signed webhook dispatcher | none |
| `Anjal.Store` | Persistence: in-memory and PostgreSQL | Npgsql |
| `Anjal.Api` | HTTP/JSON API (placeholder for PR 5) | none |
| `Anjal.Server` | Composition root host process | none |

## Build

    dotnet build

## Test

    dotnet test

## Run the demos

The two end-to-end demos prove inbound and outbound pipelines in-process,
no PostgreSQL or external network needed:

    dotnet run --project examples/Anjal.InboundEndToEnd
    dotnet run --project examples/Anjal.OutboundEndToEnd

## Run a real server

Set up the schema once:

    psql -d anjal -f tools/sql/schema.sql

Then run the server. Inbound-only:

    $env:ANJAL_POSTGRES = "Host=localhost;Database=anjal;Username=postgres;Password=YOUR-PASSWORD"
    dotnet run --project src/Anjal.Server

Inbound + outbound via a relay (recommended on hosts where port 25 is blocked):

    $env:ANJAL_POSTGRES = "Host=localhost;Database=anjal;Username=postgres;Password=YOUR-PASSWORD"
    $env:ANJAL_OUTBOUND_MODE = "relay"
    $env:ANJAL_RELAY_HOST = "smtp.your-relay.example"
    $env:ANJAL_RELAY_PORT = "587"
    dotnet run --project src/Anjal.Server

Inbound + outbound via direct MX delivery (requires port 25 outbound permitted):

    $env:ANJAL_POSTGRES = "Host=localhost;Database=anjal;Username=postgres;Password=YOUR-PASSWORD"
    $env:ANJAL_OUTBOUND_MODE = "direct"
    dotnet run --project src/Anjal.Server

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `ANJAL_BIND` | `127.0.0.1` | Bind address |
| `ANJAL_PORT` | `2525` | SMTP receiver port |
| `ANJAL_HOSTNAME` | `anjal.localhost` | Hostname in banner and EHLO |
| `ANJAL_POSTGRES` | (in-memory) | PostgreSQL connection string |
| `ANJAL_OUTBOUND_MODE` | `none` | `direct`, `relay`, or `none` |
| `ANJAL_RELAY_HOST` | - | Relay host (when mode=relay) |
| `ANJAL_RELAY_PORT` | `587` | Relay port (when mode=relay) |

## Webhook integration

When a message is accepted for a registered local-part, Anjal POSTs a
signed JSON payload to the registered webhook URL:

- `X-Anjal-Timestamp` - Unix seconds at the time of send
- `X-Anjal-Signature` - `sha256=<hex>` of HMAC-SHA256 over `timestamp + "." + body`

See PR 3's release notes or `examples/Anjal.InboundEndToEnd` for the
payload schema.

## Outbound queue

Outbound messages are queued in the `outbound_messages` table and drained
by the `OutboundWorker` background task. Failed attempts are retried with
exponential backoff (1m, 5m, 15m, 1h, 6h, 24h). Messages are permanently
marked Failed if they reach their `give_up_at` deadline (default 24
hours from creation) or receive a 5xx reply from the destination.

Sub-addressing (RFC 5233) lets a single mailbox accept time-bounded
per-case correspondence; tags are validated against the `tag_grants` table.

## Sub-addressing

A recipient of the form `local-part+tag@host` routes by `local-part`;
the `tag` is exposed in the webhook payload. Tags must be authorised by
an active `TagGrant` for the matching (local-part, tag) pair, otherwise
SMTP returns 550.

## Regenerate API docs

    python tools/gen_api_docs.py

## Pre-push verification (Windows)

    .\deploy.ps1 -Message "Your commit message"
