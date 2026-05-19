# IRoutingTable

**Namespace:** `Anjal.Routing`

Resolves SMTP recipient addresses to routing decisions. Backed by an in production; can be replaced with a stub in tests.

## Members

- **ResolveAsync** *(method)* - Look up the routing decision for a recipient address. Pure read - this method never modifies any persisted state.
