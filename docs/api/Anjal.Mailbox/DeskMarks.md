# DeskMarks

**Namespace:** `Anjal.Mailbox`

rc.27 (DES-17 §2.1): how a desk finds a conversation again. A conversation is the mails whose subjects are the same once "Re:", "Fwd:" and their kind are taken off (the rule the lists have used since rc.12) with the same person outside the desk - so two patients who both write "Appointment" are two pieces of work, not one. On a desk it is remembered by a keyed mark of the two, never the subject or the address.

## Members

- **SharedKind** *(field)* - The kind of the organisation document listing its shared mailboxes (the webmail's SharedKind).
- **AddressRegex** *(method)* - _(no description)_
- **ConversationKey** *(method)* - A subject without its reply and forward prefixes, case and spacing folded: the conversation's key.
- **FirstAddress** *(method)* - The first address in a header such as From or To, lower case; empty when there is none.
- **IsDesk** *(method)* - Whether a mailbox is one of its organisation's shared mailboxes (a desk), from the organisation's document.
- **OtherParty** *(method)* - The person outside the desk in a mail: who wrote it, or - for the desk's own reply - whom it went to.
- **ReplyPrefixRegex** *(method)* - _(no description)_
- **SpaceRegex** *(method)* - _(no description)_
- **ThreadMark** *(method)* - A conversation's mark on one desk: keyed with the pepper and the desk, so it cannot be read back or compared between desks.
