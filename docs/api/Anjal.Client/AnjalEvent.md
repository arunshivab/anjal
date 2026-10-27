# AnjalEvent

**Namespace:** `Anjal.Client`

One event (DES-14 §8): message.queued, message.delivered, inbox.received and the rest.

## Members

- **At** *(property)* - When.
- **Data** *(property)* - The event's data.
- **Id** *(property)* - The event's id (Anjal-Event-Id).
- **Message** *(property)* - The message it is about, with its tags and metadata: {id, tags, metadata}.
- **SendId** *(property)* - The send's id, or null.
- **Seq** *(property)* - Its sequence number: pass the last one seen as "since".
- **Type** *(property)* - Its type.
