# SendRequest

**Namespace:** `Anjal.Client`

One message to send (DES-14 §4).

## Members

- **ToJson** *(method)* - The body as the API reads it.
- **Attachments** *(property)* - Files.
- **Bcc** *(property)* - Bcc.
- **Cc** *(property)* - Cc.
- **From** *(property)* - From: one of the application's allowed senders.
- **Headers** *(property)* - Headers whose names begin X-.
- **Html** *(property)* - The HTML.
- **Metadata** *(property)* - String pairs that come back with status and events. Never patient details.
- **ReplyTo** *(property)* - Reply-To.
- **ScheduledAt** *(property)* - When to send, up to 7 days ahead; null for now.
- **Subject** *(property)* - The subject.
- **Tags** *(property)* - Short words to find the message by. Never patient details.
- **Template** *(property)* - A template, in place of Subject, Text and Html.
- **Text** *(property)* - The text.
- **To** *(property)* - To.
- **Unsubscribe** *(property)* - True to add List-Unsubscribe.
