# SpfDetail

**Namespace:** `Anjal.Auth`

Detailed result of an SPF check. Exposes which mechanism matched, the number of DNS lookups used (against the 10-lookup RFC 7208 limit), and the explanation string if one was provided.

## Members

- **Domain** *(property)* - Domain that was checked (the MAIL FROM domain, or HELO if MAIL FROM is empty).
- **Explanation** *(property)* - Human-readable explanation of the result, suitable for inclusion in the Authentication-Results header's comment.
- **LookupCount** *(property)* - Number of DNS lookups performed (against the RFC 7208 limit of 10).
- **MatchedMechanism** *(property)* - The SPF mechanism that produced the result (e.g. ip4:192.0.2.0/24, include:_spf.google.com, -all). Empty if Result is None or PermError.
- **PeerAddress** *(property)* - Peer IP address that was checked.
- **Result** *(property)* - The verdict.
