# DmarcReports

**Namespace:** `Anjal.Viewing`

Reading DMARC aggregate reports (rc.15, items 31 to 33): the XML as receivers send it, plain, gzipped or zipped. Nothing is fetched; the XML may not declare a DTD; sizes are capped, so a hostile report cannot exhaust memory.

## Members

- **MaxBytes** *(field)* - The largest report read, after decompression.
- **MaxDepth** *(field)* - The deepest a report's XML may nest (RFC 7489 appendix C goes five levels deep).
- **MayBeReport** *(method)* - True for an attachment that can be a report: .xml, .xml.gz, .gz or .zip, or an XML, gzip or zip type.
- **Read** *(method)* - Read a report from an attachment; null when it is not one.
