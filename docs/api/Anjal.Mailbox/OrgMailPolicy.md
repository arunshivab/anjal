# OrgMailPolicy

**Namespace:** `Anjal.Mailbox`

What an organisation lets its people do with their mail (rc.22, owner's decisions of 18 Oct 2026), kept as the organisation's "mailrules" document and read at delivery by every process.

## Members

- **Kind** *(field)* - The tenant document kind.
- **ReadAsync** *(method)* - Read an organisation's policy; Anjal's defaults when it has set none.
- **AskBeforeFilesOutside** *(property)* - Owner, 19 Oct 2026: ask once before any mail that carries files goes to an address outside the organisation, whatever the files are. Off by default; the patient and personal-information checks run either way.
- **AwayOutside** *(property)* - Whether out-of-office replies go to senders outside the organisation (item 30).
- **ForwardingOutside** *(property)* - Whether rules may forward a copy to an address outside the organisation (item 29). Forwarding inside the organisation is always allowed; every forward is logged. Individuals (an "individuals" tenant) may forward anywhere.
- **Nicknames** *(property)* - Whether people may choose the name on their mails (item 37); off means first name + last name.
