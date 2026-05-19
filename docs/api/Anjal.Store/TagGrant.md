# TagGrant

**Namespace:** `Anjal.Store`

A time-bounded authorisation for a specific "+tag" against a routing rule. Lets SIGMA grant a patient permission to send to reports+X7Y9@host for case 18472, expiring in 30 days.

## Members

- **CorrelationKey** *(property)* - Application-supplied correlation key (e.g. a case ID).
- **CreatedAt** *(property)* - When the grant was issued.
- **ExpiresAt** *(property)* - When the grant expires. Messages arriving after are rejected.
- **Id** *(property)* - Identifier assigned by the store.
- **LocalPart** *(property)* - The local-part this grant relates to.
- **Tag** *(property)* - The "+tag" string this grant authorises.
