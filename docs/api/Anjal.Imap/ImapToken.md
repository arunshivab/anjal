# ImapToken

**Namespace:** `Anjal.Imap`

One piece of an IMAP command: an atom, a string (quoted or literal), or a parenthesised list.

## Members

- **MaxDepth** *(field)* - The deepest list a command may hold; no command of a real mail app comes near it.
- **Is** *(method)* - True for an atom equal to , ignoring case.
- **Read** *(method)* - Read the tokens of one command (its tag and name included).
- **Bytes** *(property)* - A string's bytes.
- **Items** *(property)* - A list's items.
- **Kind** *(property)* - 'a' atom, 's' string, 'l' list.
- **Text** *(property)* - The text (a string's bytes as UTF-8).
