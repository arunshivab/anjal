# AuthResultsHeader

**Namespace:** `Anjal.Smtp`

Authentication-Results headers (RFC 8601). A sender can write one into a message claiming anything - "dmarc=pass" included. So on arrival every such header that names this server is removed, as RFC 8601 section 5 asks, and the one this server then adds is the only one bearing its name. Readers trust only that one (DEF-038).

## Members

- **AuthServIdOf** *(method)* - The authserv-id of a header value: the first token, before a ';' or whitespace (an optional version number may follow it).
- **FieldEnd** *(method)* - End of the field starting at , including folded lines.
- **HeaderSectionEnd** *(method)* - Offset of the blank line ending the header section (or the end).
- **RemoveClaimsBy** *(method)* - Remove every Authentication-Results field in the header section whose authserv-id is . The body and all other fields are left exactly as they were.
