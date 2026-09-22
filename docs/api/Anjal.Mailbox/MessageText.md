# MessageText

**Namespace:** `Anjal.Mailbox`

The readable text of a message, for search: the first plain-text part, or failing that the first HTML part with its markup removed. Attachments are never included. Capped at so one enormous message cannot bloat the index.

## Members

- **MaxChars** *(field)* - The most characters kept per message.
- **Decode** *(method)* - Decode with the declared charset; with none declared, UTF-8, which reads plain ASCII identically and does not mangle Indian scripts sent without a charset parameter.
- **Extract** *(method)* - Extract searchable text; empty when there is none.
