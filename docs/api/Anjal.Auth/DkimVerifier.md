# DkimVerifier

**Namespace:** `Anjal.Auth`

Verifies DKIM signatures per RFC 6376. Reuses the canonicalizer from Anjal.Dkim. Supports the algorithms rsa-sha256 and the historically-required-but-deprecated rsa-sha1 (verify-only; signing only uses rsa-sha256).

## Members

- **MaxSignaturesChecked** *(field)* - The most signatures checked on one message. RFC 6376 section 6.1 lets a verifier limit this so a message cannot make it do unbounded work.
- **MinimumKeyBits** *(field)* - The shortest RSA key accepted (RFC 8301 section 3.2).
- **#ctor** *(method)* - Construct.
- **CheckSignature** *(method)* - Verify an RSA signature against a published key, applying the RFC 8301 minimum key size. Separated from the DNS fetch so it can be exercised directly.
- **FetchPublicKeyAsync** *(method)* - Fetch the public key from the selector._domainkey.domain TXT record. Returns the base64-encoded SubjectPublicKeyInfo from the p= tag.
- **IsAligned** *(method)* - True when the signing domain and the From domain are the same or one is a subdomain of the other - the relaxed alignment DMARC checks.
- **NthFromBottom** *(method)* - The -th instance (0 = bottom-most) of a header, counting upwards, or null when there are not that many.
- **ParseTags** *(method)* - Parse a DKIM tag-list (semicolon-separated key=value pairs).
- **RemoveBTagValue** *(method)* - Return the DKIM-Signature value with the b= tag's value (but not the tag itself) emptied. Per RFC 6376 §3.7 step 5b.
- **VerifyAsync** *(method)* - Verify a message's DKIM-Signature headers, top first, up to . The result reported is a passing signature whose domain matches the From domain (the one DMARC can use); failing that, any passing signature; failing that, the first signature's result. DEF-057: only the first signature used to be checked, so a broken or unaligned first signature hid a valid aligned second one.
