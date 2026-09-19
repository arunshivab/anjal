# Greylist

**Namespace:** `Anjal.Spam`

Classic greylisting. The first time a given (client /24 or /64, sender, recipient) triplet is seen, RCPT is deferred with 451. A retry after passes and the triplet is remembered; legitimate MTAs retry, most spam cannons do not. Authenticated sessions and loopback/private clients are never greylisted. State is in-memory.

## Members

- **#ctor** *(method)* - Construct.
- **NetworkKey** *(method)* - Group clients by network so a sending pool with several outbound IPs (common for large providers) counts as one: /24 for IPv4, /64 for IPv6.
- **OnConnect** *(method)* - _(no description)_
- **OnMailFrom** *(method)* - _(no description)_
- **OnRcptTo** *(method)* - _(no description)_
- **Count** *(property)* - Number of triplets currently remembered (pending or passed).
