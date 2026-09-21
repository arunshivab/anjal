# MimeParseException

**Namespace:** `Anjal.Mime`

A message could not be parsed within Anjal's safety limits: nesting deeper than or more parts than . Derives from so every existing caller that handles malformed input handles this too; the raw message is still delivered, it is simply not parsed for display or scoring.

## Members

- **#ctor** *(method)* - Construct.
- **#ctor** *(method)* - Construct with a message.
- **#ctor** *(method)* - Construct with a message and cause.
