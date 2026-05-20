# DkimVerifier

**Namespace:** `Anjal.Auth`

Verifies DKIM signatures per RFC 6376. Reuses the canonicalizer from Anjal.Dkim. Supports the algorithms rsa-sha256 and the historically-required-but-deprecated rsa-sha1 (verify-only; signing only uses rsa-sha256).

## Members

- **#ctor** *(method)* - Construct.
- **FetchPublicKeyAsync** *(method)* - Fetch the public key from the selector._domainkey.domain TXT record. Returns the base64-encoded SubjectPublicKeyInfo from the p= tag.
- **ParseTags** *(method)* - Parse a DKIM tag-list (semicolon-separated key=value pairs).
- **RemoveBTagValue** *(method)* - Return the DKIM-Signature value with the b= tag's value (but not the tag itself) emptied. Per RFC 6376 §3.7 step 5b.
- **VerifyAsync** *(method)* - Verify the first DKIM-Signature header on a message. If multiple signatures are present, only the first is checked.
