# MessageDate

**Namespace:** `Anjal.Mime`

Dates Anjal writes into messages (v1.0.0-rc.8): an RFC 5322 date-time in the configured time zone with its true offset - India by default, so a message sent at 23:24 IST reads "23:24:33 +0530", not "17:54:33 +0000". The zone is ANJAL_TIMEZONE (an IANA name, e.g. Asia/Kolkata); an unknown zone falls back to UTC. The offset is always computed, never written as fixed text, so the time and its label cannot disagree.

## Members

- **DefaultZoneId** *(field)* - The default zone when none is configured.
- **Format** *(method)* - Format an instant for a Date header in .
- **Format** *(method)* - Format an instant for a Date header in a given zone.
- **Resolve** *(method)* - A zone from its IANA id; UTC when the id is empty or unknown.
- **Zone** *(property)* - The zone dates are written in.
