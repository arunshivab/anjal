# MailRules

**Namespace:** `Anjal.Mailbox`

Reading and judging a mailbox's rules. The rules are the mailbox's "rules" document; contact groups (for "From is in the group") are its "contact-groups" document, both written by the webmail.

## Members

- **GroupsKind** *(field)* - The document kind holding contact groups.
- **Kind** *(field)* - The document kind holding the rules.
- **MaxRules** *(field)* - At most this many rules per mailbox.
- **Evaluate** *(method)* - The first enabled rule all of whose conditions hold, or null.
- **Holds** *(method)* - Whether one condition holds for a message.
- **Parse** *(method)* - Read the rules document; an empty or broken one gives no rules.
- **ParseGroups** *(method)* - Read group names and members from the contact-groups document.
- **Write** *(method)* - Write the rules document.
