# MimePart

**Namespace:** `Anjal.Mime`

A leaf MIME entity carrying a body of bytes. The bytes stored here are always the decoded form - any Content-Transfer-Encoding has been undone. Builders re-encode them on output according to the entity's encoding header.

## Members

- **#ctor** *(method)* - Construct an empty part.
- **GetBodyAsText** *(method)* - Decode the body as text using the charset from the Content-Type header, falling back to US-ASCII. Useful for text/* parts.
- **SetBodyAsText** *(method)* - Set the body from a string using the given encoding.
- **Body** *(property)* - The decoded body bytes. Setter replaces the body entirely.
