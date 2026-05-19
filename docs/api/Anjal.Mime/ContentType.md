# ContentType

**Namespace:** `Anjal.Mime`

A parsed Content-Type header per RFC 2045 section 5. Identifies the media type of an entity body and any associated parameters such as charset or boundary.

## Members

- **#ctor** *(method)* - Construct a ContentType from its components.
- **Default** *(method)* - The default Content-Type per RFC 2045 section 5.2: text/plain; charset=us-ascii.
- **Parse** *(method)* - Parse a Content-Type header value (the part after the colon). Returns the default text/plain; charset=us-ascii per RFC 2045 section 5.2 if the input is null, empty, or unparseable.
- **ToHeaderValue** *(method)* - Serialise this Content-Type to a header value (the part after "Content-Type: ").
- **ToString** *(method)* - _(no description)_
- **Boundary** *(property)* - The boundary parameter (for multipart entities) if present.
- **Charset** *(property)* - The charset parameter if present, otherwise .
- **IsMultipart** *(property)* - Whether the media type is "multipart".
- **IsText** *(property)* - Whether the media type is "text".
- **MediaType** *(property)* - The top-level media type (e.g. "text", "multipart", "application").
- **MimeType** *(property)* - The "type/subtype" string with no parameters, lowercase per RFC 2045 section 5.1 ("type and subtype values are not case sensitive").
- **Parameters** *(property)* - Parameters from the header, e.g. charset for text bodies or boundary for multipart bodies. Keys are compared case-insensitively.
- **SubType** *(property)* - The subtype (e.g. "plain", "html", "mixed").
