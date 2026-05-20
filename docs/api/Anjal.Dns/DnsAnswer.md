# DnsAnswer

**Namespace:** `Anjal.Dns`

A single answer record. Only MX-specific data is exposed; other types are kept by RDATA but not parsed.

## Members

- **Class** *(property)* - Record class code (almost always 1 = IN).
- **MxRecord** *(property)* - Parsed MX content if is 15; otherwise null.
- **Name** *(property)* - The owner name of this record.
- **Ttl** *(property)* - Time-to-live in seconds.
- **TxtStrings** *(property)* - Parsed TXT content if is 16; otherwise null. A single DNS TXT record may contain multiple length-prefixed string chunks per RFC 1035 section 3.3.14; consumers typically concatenate them to recover the logical value (this is how SPF, DKIM, and DMARC records can exceed 255 bytes).
- **Type** *(property)* - Record type code (e.g. 15 = MX).
