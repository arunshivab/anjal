# TlsReports

**Namespace:** `Anjal.Viewing`

Reading SMTP TLS reports (RFC 8460; rc.15, item 59): the JSON receivers send - plain or gzipped - about the mail they tried to deliver to a domain, as reports to the address in its _smtp._tls record. Nothing is fetched; sizes are capped, so a hostile report cannot exhaust memory.

## Members

- **MaxBytes** *(field)* - The largest report read, after decompression.
- **MayBeReport** *(method)* - True for an attachment that can be a TLS report: .json or .json.gz, or a tlsrpt, JSON or gzip type.
- **Read** *(method)* - Read a TLS report from an attachment; null when it is not one.
