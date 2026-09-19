# CompositeMessageSink

**Namespace:** `Anjal.Smtp`

Fans one delivery out to several s and merges their results. Every sink sees every message; each decides for itself which recipients it owns. The merged outcome is if any sink accepted, otherwise if any sink reported a transient failure, otherwise . This lets a mailbox sink and a webhook-routing sink coexist: an address can be a mailbox, a webhook target, or both.

## Members

- **#ctor** *(method)* - Construct with the sinks to fan out to, in call order.
- **DeliverAsync** *(method)* - _(no description)_
- **Sinks** *(property)* - The sinks this composite fans out to, in call order.
