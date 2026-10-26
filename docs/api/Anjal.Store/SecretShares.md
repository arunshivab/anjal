# SecretShares

**Namespace:** `Anjal.Store`

rc.25 (DES-15 §6.3, D-240, D-241): Shamir's secret sharing over GF(256), 2 of 3 - the break-glass secret split among the owner, Sthakitha and a bank locker; any two rebuild it, one alone reveals nothing. Each share is its x (1, 2 or 3) and one byte per byte of the secret.

## Members

- **Combine** *(method)* - Rebuild the secret from two different shares.
- **Multiply** *(method)* - Multiply in GF(2^8).
- **Split** *(method)* - Split a secret into three shares, any two of which rebuild it.
