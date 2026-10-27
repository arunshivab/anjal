# IAppApiStore

**Namespace:** `Anjal.Store`

rc.26 (DES-14 §28.1): what the application API keeps, in both stores. Like , a store that has it is asked for it by a cast.

## Members

- **AcknowledgeAppMailAsync** *(method)* - Acknowledge one mail of an application inbox (harmless twice).
- **AddAppEventAsync** *(method)* - Add an event.
- **AppInboxWaitingAsync** *(method)* - How many mails wait unacknowledged, and when the oldest arrived.
- **CountAppOutboundElsewhereAsync** *(method)* - How many queue rows an organisation's API sends made in a period with a return path on none of its own domains (Anjal's own return domain, DES-14 §17 option 3) - for the day's sending figure.
- **CountAppSendsAsync** *(method)* - How many sends an application made since a moment (its rate windows).
- **DeleteAppSendAsync** *(method)* - Remove a send that was refused before anything was kept, so its key may be used again.
- **DueAppWebhookDeliveriesAsync** *(method)* - Webhook deliveries due now.
- **FindAppSendByKeyAsync** *(method)* - The send (or batch) an application made with an Idempotency-Key in the last 24 hours, or null.
- **FindAppSendByMessageIdAsync** *(method)* - The send of an application whose Message-ID this is, or null (replies, DES-14 §16).
- **GetAppEventAsync** *(method)* - One event.
- **GetAppInboxAddressAsync** *(method)* - The application that takes mail for an address, or null.
- **GetAppSendAsync** *(method)* - One send.
- **GetAppWebhookDeliveryAsync** *(method)* - One webhook delivery.
- **ListAppEventsAsync** *(method)* - Events after a sequence number, oldest first.
- **ListAppInboxAddressesAsync** *(method)* - An organisation's application inbox addresses.
- **ListAppInboxAsync** *(method)* - An application inbox's mail, oldest first.
- **ListAppSendsAsync** *(method)* - An application's sends, newest first.
- **ListAppWebhookDeliveriesAsync** *(method)* - An application's latest webhook deliveries, newest first.
- **ListOutboundOfAppSendAsync** *(method)* - A send's rows in the outbound queue, without their content.
- **MarkAppEventDispatchedAsync** *(method)* - Mark an event turned into deliveries.
- **PruneAppApiAsync** *(method)* - The daily clearing: events and webhook deliveries after 30 days, signatures after 10 minutes, Idempotency-Keys after 24 hours, sandbox sends after 7 days.
- **RememberSignatureAsync** *(method)* - Remember a signed request; false when the same one was seen in the last 10 minutes (a replay).
- **RemoveAppInboxAddressAsync** *(method)* - Remove an application inbox address.
- **SaveAppWebhookDeliveryAsync** *(method)* - Add or update a webhook delivery.
- **SetAppInboxAddressAsync** *(method)* - Add or change an application inbox address.
- **StartAppSendAsync** *(method)* - Record a new send. When the application already used the same Idempotency-Key in the last 24 hours, nothing is written and that send is returned with Existing true.
- **UndispatchedAppEventsAsync** *(method)* - Events the webhook worker has not yet turned into deliveries, oldest first.
- **UpdateAppSendAsync** *(method)* - Set a send's status, response and Message-ID.
