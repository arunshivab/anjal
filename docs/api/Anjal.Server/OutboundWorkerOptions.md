# OutboundWorkerOptions

**Namespace:** `Anjal.Server`

Configuration for an .

## Members

- **BatchSize** *(property)* - Maximum messages to lease per iteration.
- **OnFinalFailure** *(property)* - Called once when a message has failed for good - refused permanently, or retried until its give-up time. Used to file a bounce notice in the sender's INBOX. Failures inside it are logged and do not affect the queue.
- **PollInterval** *(property)* - How long to sleep between batches when the queue is empty.
