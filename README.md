# Anjal (அஞ்சல்)

A .NET 10 mail server library. Protocol code (MIME, SMTP, DNS) and the
HTTP API are hand-written with no external NuGet dependencies. The
PostgreSQL store layer uses [Npgsql](https://www.npgsql.org/).

## Status

**v0.4.0** - bidirectional mail + HTTP/JSON API. Apps like SIGMA and
Lipi HIS can drive Anjal entirely through HTTP: register routing rules,
issue tag grants, enqueue outbound mail, and fetch message status.

No TLS, DKIM, SPF, or DMARC yet - Phase 2.

## Modules

| Module | Purpose | NuGet |
|---|---|---|
| `Anjal.Mime` | MIME parser and builder (RFC 5322, RFC 2045-2049) | none |
| `Anjal.Smtp` | SMTP receiver and sender (RFC 5321) | none |
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

Three end-to-end demos prove the inbound, outbound, and full-stack
HTTP pipelines in-process, no PostgreSQL or external network needed:

    dotnet run --project examples/Anjal.InboundEndToEnd
    dotnet run --project examples/Anjal.OutboundEndToEnd
    dotnet run --project examples/Anjal.ApiClient

The third demo brings up the SMTP receiver, outbound worker, and API
server in one process and uses `HttpClient` to call the API end-to-end.

## Run a real server

Set up the schema once:

    psql -d anjal -f tools/sql/schema.sql

Then run the server. Inbound-only (no outbound, no API):

    $env:ANJAL_POSTGRES = "Host=localhost;Database=anjal;Username=postgres;Password=YOUR-PASSWORD"
    dotnet run --project src/Anjal.Server

Full stack (recommended): SMTP receiver + outbound relay + HTTP API:

    $env:ANJAL_POSTGRES      = "Host=localhost;Database=anjal;Username=postgres;Password=YOUR-PASSWORD"
    $env:ANJAL_OUTBOUND_MODE = "relay"
    $env:ANJAL_RELAY_HOST    = "smtp.your-relay.example"
    $env:ANJAL_RELAY_PORT    = "587"
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

## HTTP API

All endpoints require `Authorization: Bearer <ANJAL_API_TOKEN>` unless
the configured token is empty (test-only mode).

### Routing rules

    POST   /api/routing-rules         { localPart, webhookUrl, webhookSecret }  -> 200 RoutingRuleResponse
    GET    /api/routing-rules                                                    -> 200 [RoutingRuleResponse]
    DELETE /api/routing-rules/{localPart}                                        -> 204 / 404

### Tag grants

    POST   /api/tag-grants            { localPart, tag, correlationKey, ttlSeconds }
                                                                                 -> 200 TagGrantResponse

### Outbound

    POST   /api/outbound              { envelopeFrom, envelopeTo, subject?, bodyText?, rawBytesBase64?, giveUpHours? }
                                                                                 -> 202 OutboundResponse
    GET    /api/outbound/{id}                                                    -> 200 OutboundResponse / 404

### Inbound

    GET    /api/inbound/{id}                                                     -> 200 InboundResponse / 404

### Error responses

All non-2xx responses share a uniform body:

    { "error": "machine_code", "message": "human-readable description" }

Codes: `unauthorized` (401), `not_found` (404), `method_not_allowed`
(405), `payload_too_large` (413), `invalid_request` / `invalid_json`
(400), `internal_error` (500).

## Webhook integration

When a message is accepted for a registered local-part, Anjal POSTs a
signed JSON payload to the registered webhook URL:

- `X-Anjal-Timestamp` - Unix seconds at the time of send
- `X-Anjal-Signature` - `sha256=<hex>` of HMAC-SHA256 over `timestamp + "." + body`

See `examples/Anjal.InboundEndToEnd` for the payload schema.

## Outbound queue

Outbound messages are queued in the `outbound_messages` table and drained
by the `OutboundWorker` background task. Failed attempts retry with
exponential backoff (1m, 5m, 15m, 1h, 6h, 24h). Messages are permanently
marked Failed if they reach their `give_up_at` deadline (default 24
hours from creation) or receive a 5xx reply.

## Sub-addressing

A recipient of the form `local-part+tag@host` routes by `local-part`;
the `tag` is exposed in the webhook payload. Tags must be authorised by
an active `TagGrant` for the matching `(localPart, tag)` pair, otherwise
SMTP returns 550.

## Regenerate API docs

    python tools/gen_api_docs.py

## Pre-push verification (Windows)

    .\deploy.ps1 -Message "Your commit message"
