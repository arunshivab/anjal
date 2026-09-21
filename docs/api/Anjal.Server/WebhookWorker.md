# WebhookWorker

**Namespace:** `Anjal.Server`

Delivers queued webhook notifications. Each job is leased, its payload rebuilt from the stored inbound message, signed with the routing rule's current secret, and sent. A 2xx answer completes it; anything else is retried on the schedule 1 s, 5 s, 25 s, 125 s, then every 10 minutes until the job's give-up time. Every attempt is logged in the existing webhook attempt table. The queue lives in the database, so a restart loses nothing; the worker is also woken immediately when a job is queued, so a healthy receiver hears about a message within milliseconds rather than at the next poll.

## Members

- **GiveUpAfter** *(field)* - How long a notification is retried before it is marked failed.
- **RetrySchedule** *(field)* - Delay before each retry, by attempt number; the last entry repeats.
- **#ctor** *(method)* - Construct.
- **Dispose** *(method)* - _(no description)_
- **RunAsync** *(method)* - Run until cancelled.
- **RunOnceAsync** *(method)* - Process one batch of due jobs. Returns how many were attempted.
- **Wake** *(method)* - Signal that a job was queued, so it is picked up now.
