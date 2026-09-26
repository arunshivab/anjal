# Greylist

**Namespace:** `Anjal.Spam`

Classic greylisting. The first time a given (client /24 or /64, sender, recipient) triplet is seen, RCPT is deferred with 451. A retry after passes and the triplet is remembered; legitimate MTAs retry, most spam cannons do not. Authenticated sessions and loopback/private clients are never greylisted, nor is a sender that vouches for. State is kept in memory and, when is set, in a file that survives restarts. Every deferral and every first pass is logged (decision 2B and DEF-059, 26 Sep 2026).

## Members

- **#ctor** *(method)* - Construct.
- **Flush** *(method)* - Write the state file now, if one is configured and anything changed. The server calls this on shutdown so an orderly restart loses nothing.
- **NetworkKey** *(method)* - Group clients by network so a sending pool with several outbound IPs (common for large providers) counts as one: /24 for IPv4, /64 for IPv6.
- **OnConnectAsync** *(method)* - _(no description)_
- **OnMailFromAsync** *(method)* - _(no description)_
- **OnRcptTo** *(method)* - Synchronous RCPT TO check.
- **OnRcptToAsync** *(method)* - _(no description)_
- **Save** *(method)* - One line per triplet: key, first seen, last seen (UTC ticks), passed. Written to a temporary file and moved into place, so a crash mid-write leaves the previous file intact.
- **Shrink** *(method)* - Forget expired triplets now; if still full, the least recently seen tenth.
- **Count** *(property)* - Number of triplets currently remembered (pending or passed).
