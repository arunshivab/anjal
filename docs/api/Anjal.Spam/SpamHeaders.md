# SpamHeaders

**Namespace:** `Anjal.Spam`

Header names written by and read by the mailbox sink. They are prepended to the message bytes the same way Authentication-Results is, so they survive into the Maildir file and can be inspected in any client.

## Members

- **Reasons** *(field)* - Comma-separated CODE(points) list.
- **Score** *(field)* - Integer score.
- **Prepend** *(method)* - Prepend the score and reasons headers to raw message bytes. Existing headers of the same name (which a sender could forge) are not removed, but ours come first and reads the first occurrence.
- **ScoreOf** *(method)* - Read the score header from a parsed message. Returns 0 when absent or unparsable.
