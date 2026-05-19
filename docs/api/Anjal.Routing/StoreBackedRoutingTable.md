# StoreBackedRoutingTable

**Namespace:** `Anjal.Routing`

Default implementation of backed by a store. Sub-addressing semantics: a recipient with no "+tag" matches the rule for its local-part directly. A recipient with a "+tag" matches the rule for its local-part only if there is an active for the (local-part, tag) pair.

## Members

- **#ctor** *(method)* - Construct the routing table.
- **ResolveAsync** *(method)* - _(no description)_
