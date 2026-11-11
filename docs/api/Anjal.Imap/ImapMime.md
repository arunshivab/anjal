# ImapMime

**Namespace:** `Anjal.Imap`

rc.28 (DES-20 §3): a message's structure read from its raw bytes, as IMAP needs it - where each part's header and body start and end, its type, parameters and encoding - so BODYSTRUCTURE, ENVELOPE and BODY[section] answer with the exact bytes the message holds.

## Members

- **MaxDepth** *(field)* - The deepest part opened; the same limit as the inbound parser's.
- **BodyBytes** *(method)* - The body bytes.
- **Envelope** *(method)* - The ENVELOPE of this message.
- **Header** *(method)* - A header field's value (the first), unfolded; empty when absent.
- **HeaderBytes** *(method)* - The header bytes, with the blank line after them.
- **HeaderFields** *(method)* - The header fields named (or all but those named), with a blank line at the end.
- **Parse** *(method)* - Read a message.
- **PartAt** *(method)* - The part a section number names ("1", "2.3"); null when there is none.
- **Structure** *(method)* - The BODYSTRUCTURE (with extension data) or BODY (without) of this part.
- **BodyEnd** *(property)* - Where its body ends.
- **BodyStart** *(property)* - Where its body starts (just after the blank line).
- **Children** *(property)* - The parts of a multipart.
- **HeaderStart** *(property)* - Where this part's header starts.
- **Headers** *(property)* - The header fields, unfolded, in order.
- **Message** *(property)* - The message inside a message/rfc822 part.
- **Parameters** *(property)* - The Content-Type parameters, names in lower case.
- **Raw** *(property)* - The whole message's bytes.
- **Subtype** *(property)* - The subtype, lower case ("plain").
- **Type** *(property)* - The type, lower case ("text").
