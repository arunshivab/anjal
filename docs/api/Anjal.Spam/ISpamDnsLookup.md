# ISpamDnsLookup

**Namespace:** `Anjal.Spam`

The DNS questions the scorer asks. Abstracted so tests can answer them without the network and so lookups can be capped or cached. Implementations must never throw; "unknown" is reported as and is scored as neutral.

## Members

- **CanReceiveMailAsync** *(method)* - Does the domain have at least one MX record, or failing that an A/AAAA record?
- **HasReverseDnsAsync** *(method)* - Does the IP have a reverse (PTR) record?
- **ResolvesAsync** *(method)* - Does the host name resolve to at least one address?
