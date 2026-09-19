# SystemSpamDnsLookup

**Namespace:** `Anjal.Spam`

Default lookups: MX via , A/AAAA and PTR via . Each question is capped at so a slow resolver cannot stall delivery.

## Members

- **#ctor** *(method)* - Construct.
- **CanReceiveMailAsync** *(method)* - _(no description)_
- **HasReverseDnsAsync** *(method)* - _(no description)_
- **ResolvesAsync** *(method)* - _(no description)_
- **Timeout** *(property)* - Per-question timeout. Default 3 seconds.
