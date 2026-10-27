# SendingDay

**Namespace:** `Anjal.Mailbox`

rc.26 (item 99, D-219): the organisation's sends per day (the operator's figure, 2,000 by default) counted for every route together - webmail, a mail app on port 587, an application's SMTP key and the application API. One count per organisation per day, in Anjal's own day (ANJAL_TIMEZONE, India by default): every copy queued to an outside address counts, so a mail to five outside people counts five.

## Members

- **DefaultLimit** *(field)* - The default figure when the operator has not set one.
- **OpsKind** *(field)* - The organisation document the figure is kept in (the operator's record).
- **DayStart** *(method)* - The start of Anjal's day containing a moment.
- **OverText** *(method)* - The reply to a mail app over the figure.
- **RefusedAsync** *(method)* - Whether more copies may go today; null when they may, else the words.
- **StateAsync** *(method)* - An organisation's figure and today's use.
