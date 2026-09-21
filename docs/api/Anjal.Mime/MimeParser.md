# MimeParser

**Namespace:** `Anjal.Mime`

Parses a sequence of bytes into a . The parser is line-oriented and tolerates either CRLF or bare LF line endings. It is pragmatic about real-world input: malformed individual headers or truncated multipart bodies are accepted, with as much of the structure preserved as possible.

## Members

- **MaxNestingDepth** *(field)* - The deepest multipart nesting accepted. Real mail rarely exceeds five or six levels; thirty-two leaves ample room while keeping the recursion far from the stack limit. Without a bound, a 7 MB message of nested multiparts - well under the size limit - overflows the stack, which no catch block can intercept, and takes the whole server process down.
- **MaxParts** *(field)* - The most MIME parts accepted in one message, across every level. Bounds the work a single message can cause even when it stays shallow.
- **Parse** *(method)* - Parse a byte array into a message.
- **Parse** *(method)* - Parse a UTF-8 encoded string into a message. Convenience overload.
- **Parse** *(method)* - Parse from a stream by reading it to the end first.
