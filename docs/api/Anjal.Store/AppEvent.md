# AppEvent

**Namespace:** `Anjal.Store`

rc.26 (DES-14 §8): one event of an application's, for /events and the webhooks.

## Members

- **ForOutbound** *(method)* - The event types an outbound row's new state gives.
- **OutboundData** *(method)* - The data of an outbound row's event: who, the reply, the attempt.
- **AppId** *(property)* - The application.
- **At** *(property)* - When.
- **Data** *(property)* - The event's data, a JSON object.
- **Id** *(property)* - The event's id (Anjal-Event-Id).
- **SendId** *(property)* - The send it is about, or null.
- **Seq** *(property)* - The order events happened in (the /events cursor).
- **TenantId** *(property)* - The organisation.
- **Type** *(property)* - message.queued, message.delivered and the rest.
