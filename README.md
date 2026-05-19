# Anjal (அஞ்சல்)

A zero-dependency .NET 10 mail server library.

## Status

**Phase 1 - localhost development.** No TLS. No public hosting. No NuGet
dependencies in production code (test projects use xUnit only).

## Modules

| Module | Purpose |
|---|---|
| `Anjal.Mime` | MIME parser and builder (RFC 5322, RFC 2045-2049) |
| `Anjal.Smtp` | SMTP receiver and sender (RFC 5321) |
| `Anjal.Dns` | DNS MX record resolver |
| `Anjal.Routing` | Address routing table - maps inbound mailboxes to webhooks |
| `Anjal.Store` | Message and attachment storage (SQL Server) |
| `Anjal.Api` | HTTP/JSON API for application integration |
| `Anjal.Server` | Composition root host process |

## Build

    dotnet build

## Test

    dotnet test

## Regenerate API docs

    python tools/gen_api_docs.py

## Pre-push verification (Windows)

    .\deploy.ps1 -Message "Your commit message"

This runs restore, build, test, doc regeneration, and format check before
committing and pushing. CI will fail anyway if any of these are stale, so
running it locally is faster than waiting on a failed GitHub Actions run.

## CI gates on `main`

Branch protection requires these four checks to pass:

- `build` on `ubuntu-latest`
- `build` on `windows-latest`
- `build` on `macos-latest`
- `style`
- `docs`

## Architecture

Anjal is a transactional mail relay - it sends and receives mail on behalf
of applications, not on behalf of humans. There is no end-user inbox UI.

```
                  +-----------+        +-----------+
   App (SIGMA) -->|  Anjal    |<------>| Internet  |
                  |  Server   |        | (Gmail,   |
                  | (HTTP API)|        |  Outlook, |
                  +-----------+        |  partners)|
                                       +-----------+
```

Inbound mail flow: SMTP receiver -> MIME parse -> routing table lookup ->
webhook POST to the owning application.

Outbound mail flow: HTTP API -> MIME build -> outbound queue ->
DNS MX lookup -> SMTP send to remote server.
