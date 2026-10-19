# MailRule

**Namespace:** `Anjal.Mailbox`

A person's rule: when every condition holds for an arriving message, the actions are done. Rules run in order, top first; the first that matches is the one applied. Mail Anjal has put in Junk is never moved out of it by a rule.

## Members

- **Conditions** *(property)* - The conditions; all must hold.
- **Enabled** *(property)* - Whether it runs.
- **Flag** *(property)* - Flag it on arrival.
- **ForwardTo** *(property)* - Send a copy on to this address (rc.22, item 29, owner 18 Oct 2026): forwarding is only by rule, and the original always stays in the mailbox. Empty for no forwarding.
- **Id** *(property)* - Identifier.
- **MarkRead** *(property)* - Mark it read on arrival.
- **MoveTo** *(property)* - The folder to file the message in; empty leaves it in INBOX.
- **Name** *(property)* - The rule's name.
