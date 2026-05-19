# MxRecord

**Namespace:** `Anjal.Dns`

A single MX record returned by a DNS resolver. RFC 1035 section 3.3.9. Lower priority is more preferred; senders try priority 0 first.

## Members

- **ToString** *(method)* - _(no description)_
- **Exchange** *(property)* - Hostname of the mail exchanger. Always lowercase.
- **Priority** *(property)* - Preference value (lower is more preferred).
