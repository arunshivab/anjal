# MailAddress

**Namespace:** `Anjal.Mime`

A single mail address: a local-part + "@" + domain, optionally accompanied by a human-readable display name. See RFC 5322 section 3.4.

## Members

- **#ctor** *(method)* - Construct a mail address. The address must contain exactly one "@".
- **Equals** *(method)* - _(no description)_
- **Equals** *(method)* - _(no description)_
- **FromParts** *(method)* - Construct a mail address from explicit local-part and domain parts.
- **GetHashCode** *(method)* - _(no description)_
- **ToString** *(method)* - _(no description)_
- **Address** *(property)* - The bare "local-part@domain" form, with no display name and no angle brackets.
- **DisplayName** *(property)* - Optional human-readable display name. Empty string if not provided.
- **Domain** *(property)* - The domain part of the address (right of the "@").
- **LocalPart** *(property)* - The local-part of the address (left of the "@").
