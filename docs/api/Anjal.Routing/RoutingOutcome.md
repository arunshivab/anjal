# RoutingOutcome

**Namespace:** `Anjal.Routing`

Possible outcomes when routing an inbound recipient address.

## Members

- **Accepted** *(field)* - A matching routing rule was found and the message should be delivered.
- **NoSuchMailbox** *(field)* - No routing rule matches the local-part. Reject with SMTP 550.
- **TagExpired** *(field)* - The address has a tag whose grant has expired. Reject with SMTP 550.
- **TagNotAuthorised** *(field)* - The address has a tag, but the tag is not authorised by an active grant. Reject with SMTP 550.
