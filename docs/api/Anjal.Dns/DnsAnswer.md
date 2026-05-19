# DnsAnswer

**Namespace:** `Anjal.Dns`

A single answer record. Only MX-specific data is exposed; other types are kept by RDATA but not parsed.

## Members

- **Class** *(property)* - Record class code (almost always 1 = IN).
- **MxRecord** *(property)* - Parsed MX content if is 15; otherwise null.
- **Name** *(property)* - The owner name of this record.
- **Ttl** *(property)* - Time-to-live in seconds.
- **Type** *(property)* - Record type code (e.g. 15 = MX).
