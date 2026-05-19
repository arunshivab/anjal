# MimeHeader

**Namespace:** `Anjal.Mime`

A single header field of a MIME entity. Header field names are case-insensitive per RFC 5322 section 1.2.2; the original casing is preserved in but equality comparisons use case-insensitive matching.

## Members

- **#ctor** *(method)* - Construct a header from its name and value. The value must already be unfolded - parsers are responsible for joining continuation lines before constructing this object.
- **Equals** *(method)* - _(no description)_
- **Equals** *(method)* - _(no description)_
- **GetHashCode** *(method)* - _(no description)_
- **ToString** *(method)* - _(no description)_
- **Name** *(property)* - The field name in the casing it was created with. Compare using .
- **Value** *(property)* - The field value with continuation folding removed. May be empty.
