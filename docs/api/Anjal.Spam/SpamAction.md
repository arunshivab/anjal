# SpamAction

**Namespace:** `Anjal.Spam`

What the filter does with a message that scores as spam.

## Members

- **Junk** *(field)* - Accept and mark; the mailbox sink files it in Junk. Default.
- **Reject** *(field)* - Refuse at SMTP time with 550. Only after the scoring has been validated against real mail.
