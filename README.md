# Anjal (அஞ்சல்)

A .NET 10 mail server library. Protocol code (MIME, SMTP, DNS) is
hand-written with no NuGet dependencies. The PostgreSQL store layer
uses [Npgsql](https://www.npgsql.org/).

## Status

**v0.2.0** - inbound pipeline end-to-end on localhost. No TLS, no public
hosting yet. Hand off accepted messages to application webhooks signed
with HMAC-SHA256.

## Modules

| Module | Purpose | NuGet |
|---|---|---|
| `Anjal.Mime` | MIME parser and builder (RFC 5322, RFC 2045-2049) | none |
| `Anjal.Smtp` | SMTP receiver (RFC 5321) | none |
| `Anjal.Dns` | DNS MX record resolver (placeholder for PR 5) | none |
| `Anjal.Routing` | Address routing table + signed webhook dispatcher | none |
| `Anjal.Store` | Persistence: in-memory and PostgreSQL | Npgsql |
| `Anjal.Api` | HTTP/JSON API for application integration (placeholder) | none |
| `Anjal.Server` | Composition root host process | none |

## Build

    dotnet build

## Test

    dotnet test

## Run the inbound end-to-end demo

The demo runs entirely in-process - no PostgreSQL or external network
needed. It spins up an `HttpListener` as a stub webhook, starts the SMTP
server on an ephemeral port, replays an SMTP transaction, and prints the
parsed message and signed webhook delivery:

    dotnet run --project examples/Anjal.InboundEndToEnd

## Run a real server

Set up the schema once:

    psql -d anjal -f tools/sql/schema.sql

Then run the server:

    $env:ANJAL_POSTGRES = "Host=localhost;Database=anjal;Username=postgres;Password=YOUR-PASSWORD"
    dotnet run --project src/Anjal.Server

Environment variables:

| Variable | Default | Purpose |
|---|---|---|
| `ANJAL_BIND` | `127.0.0.1` | Bind address |
| `ANJAL_PORT` | `2525` | TCP port |
| `ANJAL_HOSTNAME` | `anjal.localhost` | Hostname advertised in SMTP banner |
| `ANJAL_POSTGRES` | unset (in-memory) | PostgreSQL connection string |

## Webhook integration

When a message is accepted for a registered local-part, Anjal POSTs a
signed JSON payload to the registered webhook URL. Headers:

- `X-Anjal-Timestamp` - Unix seconds at the time of send
- `X-Anjal-Signature` - `sha256=<hex>` over `timestamp + "." + body`

To validate on the receiver side, recompute the HMAC-SHA256 using your
shared secret and compare against the header. The payload schema:

```json
{
  "inboundMessageId": "uuid",
  "recipient": "reports+CASE-18472@anjal.example",
  "localPart": "reports",
  "tag": "case-18472",
  "correlationKey": "case:18472",
  "envelopeFrom": "patient@gmail.com",
  "subject": "Lab report for case 18472",
  "messageId": "abc@example",
  "receivedAt": "2026-05-19T12:00:00+00:00",
  "rawBytesBase64": "..."
}
```

## Sub-addressing

Anjal uses RFC 5233 sub-addressing. A recipient of the form
`local-part+tag@host` routes by `local-part`; the `tag` is exposed in
the webhook payload. Tags must be authorised by a `TagGrant` for the
matching (local-part, tag) pair before a tagged message will be
accepted; otherwise SMTP returns 550.

## Regenerate API docs

    python tools/gen_api_docs.py

## Pre-push verification (Windows)

    .\deploy.ps1 -Message "Your commit message"
