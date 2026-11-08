# AppGiveUp

**Namespace:** `Anjal.Mailbox`

rc.27 (item 72.8, D-314): how long Anjal keeps trying to deliver an application's mail - chosen per application, 5 days (the queue's own) by default. A patient reminder can stop after 4 hours; a report may keep trying.

## Members

- **Choices** *(field)* - The hours an application may choose.
- **DefaultHours** *(field)* - The default: the queue's own 5 days.
- **ForKey** *(method)* - The give-up time of the application an SMTP key belongs to, from the organisation's applications document ("apps"); the default when the key is not an application's.
- **Of** *(method)* - The time for a chosen number of hours; the default for anything not on the list.
