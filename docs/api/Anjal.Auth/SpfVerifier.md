# SpfVerifier

**Namespace:** `Anjal.Auth`

Verifies SPF records per RFC 7208. Supports the mechanisms all, ip4, ip6, a, mx, include, exists, and the redirect= modifier. The ptr mechanism is treated as Neutral (deprecated by RFC 7208 §5.5). Macro expansion (exp=) is not implemented; such records evaluate based on their literal tokens, which is correct for the vast majority of modern records.

## Members

- **MaxLookups** *(field)* - Maximum total DNS lookups during a single check (RFC 7208 §4.6.4).
- **MaxVoidLookups** *(field)* - Lookups returning no answer allowed per evaluation (RFC 7208 section 4.6.4).
- **#ctor** *(method)* - Construct.
- **CheckAsync** *(method)* - Run an SPF check. The is the IP that connected to Anjal; is from the SMTP MAIL FROM (RFC 7208 calls this the "MAIL FROM identity"); if MAIL FROM is empty (bounce), use the HELO identity instead.
- **EvaluateDomainAsync** *(method)* - Recursive evaluation for include/redirect chains. Each include counts against the global lookup limit tracked on .
- **EvaluateMechanismAsync** *(method)* - Evaluate a single mechanism token (without the qualifier prefix). Returns (matched, detailString) where detailString is suitable for the SpfDetail.MatchedMechanism field.
- **FetchSpfRecordAsync** *(method)* - Fetch and select the SPF TXT record for a domain. RFC 7208 §4.5: at most one v=spf1 record is permitted; multiple → PermError.
- **MatchesAOrMxAsync** *(method)* - Resolve a domain via system DNS (A records for IPv4 or AAAA for IPv6) or via Anjal.Dns for MX, and check if any resulting IP matches the peer.
- **MatchesCidr** *(method)* - Match an IP address against a CIDR specification. is in the form "192.0.2.0/24" or "192.0.2.5" (no mask = all bits). Returns false if spec is malformed.
- **ParseADomainAndCidr** *(method)* - Parse the optional :domain and /cidr suffix from an a or mx mechanism.
