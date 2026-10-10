# ServiceRecords

**Namespace:** `Anjal.Server`

rc.15 (items 59 and 61): what the mail server keeps for the dashboards. A refused submission by an application's key is written to the activity log, so its organisation sees it in the Applications box; and the server's refusal counters are added up per day, so the operator sees the attacks refused. Neither ever holds mail or what was typed.

## Members

- **RefusalCounters** *(field)* - The counters added up per day, with the words the operator's dashboard shows.
- **RefusalsKind** *(field)* - The kind of the service record holding the refusal counts per day.
- **AddAsync** *(method)* - Add counts to a day's totals in the refusals record.
- **RefusalRecorder** *(method)* - The listener for refused submissions: when the user name is a key the service made (an application's), the refusal goes to the activity log as smtp.submission.refused. Other names - guesses - are only counted.
- **RunRefusalCountsAsync** *(method)* - Every ten minutes, add what the refusal counters gained to today's totals in the service record (kept for 400 days). A restart starts the counters at nought again; nothing is counted twice.
