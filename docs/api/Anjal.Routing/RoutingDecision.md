# RoutingDecision

**Namespace:** `Anjal.Routing`

The result of asking to route a recipient. Encapsulates both success (matched rule + optional grant) and failure (an outcome explaining why the address won't be accepted).

## Members

- **Address** *(property)* - The resolved address components used for the lookup.
- **Grant** *(property)* - The active tag grant when the recipient carried a tag, otherwise .
- **Outcome** *(property)* - The outcome of the routing attempt.
- **Rule** *(property)* - The matched routing rule, present only when is .
- **SmtpReplyText** *(property)* - The SMTP reply text appropriate for the outcome, suitable for inclusion in a 550 response.
