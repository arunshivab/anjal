# DmarcEvaluator

**Namespace:** `Anjal.Auth`

Evaluates DMARC per RFC 7489. Fetches the _dmarc.<domain> TXT record, parses the policy, and checks alignment between the From header's domain and the SPF/DKIM identifiers.

## Members

- **#ctor** *(method)* - Construct.
- **EvaluateAsync** *(method)* - Evaluate DMARC for a message. Pass the From header's domain, the MAIL FROM domain that SPF was checked against, and the SPF + DKIM verdicts so we can apply the alignment + policy rules.
- **IsAligned** *(method)* - Check whether aligns with under the given mode. Strict = exact match (case-insensitive). Relaxed = organizational domain match - the candidate must equal the From domain or end with .<fromDomain>.
- **LookupDmarcAsync** *(method)* - Look up the DMARC record at _dmarc.<domain>. If absent, per RFC 7489 §6.6.3 the lookup walks up to the organizational domain. We approximate this by also trying the registered-domain portion (everything after the first dot) once.
- **ParseRecord** *(method)* - Parse a DMARC TXT record into structured form.
