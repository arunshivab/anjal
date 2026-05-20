# DnsResolver

**Namespace:** `Anjal.Dns`

Hand-rolled DNS resolver for MX records. Speaks RFC 1035 directly over UDP/53 to a configurable nameserver. No NuGet dependency. If the response is truncated (TC bit set) the resolver retries over TCP per section 4.2.2.

## Members

- **#ctor** *(method)* - Construct a resolver bound to a specific nameserver address.
- **BuildQuery** *(method)* - Encode a DNS query message. Exposed as static for use by test harnesses.
- **CreateFromSystem** *(method)* - Construct a resolver pointing at the first IPv4 system DNS server, falling back to 1.1.1.1 if none can be determined.
- **LookupTxtAsync** *(method)* - Look up TXT records for a domain. Returns the list of TXT records, each as a single concatenated string (per RFC 6376 / 7208 / 7489 conventions, multiple length-prefixed chunks in one TXT record are joined). Returns an empty list if the domain has no TXT records or does not exist (NXDOMAIN treated as empty).
- **ResolveMxAsync** *(method)* - Resolve MX records for a domain. Returns records sorted by ascending priority (most-preferred first). An empty list means no MX records were returned for the domain.
