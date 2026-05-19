# TagGrantRequest

**Namespace:** `Anjal.Api.Dto`

Request body for POST /api/tag-grants: issue a time-bounded authorisation for localPart+tag.

## Members

- **CorrelationKey** *(property)* - Application-supplied correlation key (e.g. a case ID).
- **LocalPart** *(property)* - The local-part the grant applies to.
- **Tag** *(property)* - The "+tag" string this grant authorises.
- **TtlSeconds** *(property)* - Time-to-live in seconds. The grant expires this far in the future.
