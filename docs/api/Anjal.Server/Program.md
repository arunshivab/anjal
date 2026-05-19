# Program

**Namespace:** `Anjal.Server`

Composition root for the Anjal mail server host process. Wires Store + Routing + Smtp + Mime together and runs until cancellation. Environment variables: ANJAL_BIND - bind address, default "127.0.0.1" ANJAL_PORT - tcp port, default 2525 ANJAL_HOSTNAME - hostname for SMTP banner, default "anjal.localhost" ANJAL_POSTGRES - PostgreSQL connection string. If unset, an in-memory store is used (suitable for demos).

## Members

- **Main** *(method)* - Entry point.
