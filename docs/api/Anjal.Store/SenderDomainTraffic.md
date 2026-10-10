# SenderDomainTraffic

**Namespace:** `Anjal.Store`

Outbound mail from one sending domain in a period (DES-11 D8: sudden rises in sent and bounced mail).

## Members

- **#ctor** *(method)* - Outbound mail from one sending domain in a period (DES-11 D8: sudden rises in sent and bounced mail).
- **Domain** *(property)* - The envelope sender's domain, lower case.
- **Failed** *(property)* - Given up on (bounced).
- **Queued** *(property)* - Everything queued from the domain in the period.
- **Sent** *(property)* - Accepted by the receiving server.
- **Waiting** *(property)* - Still waiting or retrying.
