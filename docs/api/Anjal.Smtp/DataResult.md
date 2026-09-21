# DataResult

**Namespace:** `Anjal.Smtp.SmtpSession`

The outcome of reading a DATA body.

## Members

- **BareDot** *(property)* - A lone "." line with non-CRLF line breaks: refused, and the session closed.
- **Dropped** *(property)* - The body could not be read: the connection must close.
- **Oversized** *(property)* - The body was read in full but exceeded the size limit.
