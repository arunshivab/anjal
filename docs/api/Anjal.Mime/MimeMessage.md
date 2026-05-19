# MimeMessage

**Namespace:** `Anjal.Mime`

A top-level RFC 5322 message. Combines a single root MIME entity with convenience accessors for the well-known headers (From, To, Subject etc.)

## Members

- **#ctor** *(method)* - Construct an empty message with a default as its body.
- **#ctor** *(method)* - Construct a message with a specific root entity.
- **Body** *(property)* - The root MIME entity. May be a for simple single-body messages or a for messages with attachments or alternative renderings.
- **Cc** *(property)* - The parsed addresses from the Cc header. Empty list if absent.
- **Date** *(property)* - The Date header value as a string. Returns empty string if absent. Date parsing into is left to a future extension to keep this type focused.
- **From** *(property)* - The parsed addresses from the From header. Empty list if absent.
- **Headers** *(property)* - The headers of the root entity. Convenience shortcut for Body.Headers.
- **MessageId** *(property)* - The Message-ID header value with angle brackets stripped, or empty.
- **Subject** *(property)* - The decoded Subject header, or empty string if absent.
- **To** *(property)* - The parsed addresses from the To header. Empty list if absent.
