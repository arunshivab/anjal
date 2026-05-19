# MimeParser

**Namespace:** `Anjal.Mime`

Parses a sequence of bytes into a . The parser is line-oriented and tolerates either CRLF or bare LF line endings. It is pragmatic about real-world input: malformed individual headers or truncated multipart bodies are accepted, with as much of the structure preserved as possible.

## Members

- **Parse** *(method)* - Parse a byte array into a message.
- **Parse** *(method)* - Parse a UTF-8 encoded string into a message. Convenience overload.
- **Parse** *(method)* - Parse from a stream by reading it to the end first.
