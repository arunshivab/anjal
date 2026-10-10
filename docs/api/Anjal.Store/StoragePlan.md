# StoragePlan

**Namespace:** `Anjal.Store`

An organisation's storage plan (DES-11 D2, owner 10 Oct 2026), chosen by the operator in the Anjal console: person: every mailbox gets ; shared: one total, , that all its people draw from; mixed: every mailbox gets , and when that is full it draws from a shared reserve of ; none: Anjal's default, 1 GB a mailbox. The organisation's administrator may give some people a smaller limit (), never a larger one. A mailbox's own limit is kept in (0 when it has none of its own, under a shared plan without a smaller limit); the shared total and the reserve are checked on top. The limit is enforced: a full mailbox receives nothing (outside senders are told "452 mailbox full" and try again later) and cannot send, except Anjal's own security mail, which always arrives.

## Members

- **Kind** *(field)* - The organisation document that keeps the plan.
- **Plans** *(field)* - The plans.
- **OwnLimit** *(method)* - A mailbox's own limit under this plan, the administrator's smaller limit taken in; 0 for none of its own.
- **Problem** *(method)* - What is wrong with the plan, or null when it can be saved.
- **ReadAsync** *(method)* - An organisation's plan; none when it has not been chosen.
- **StateOfAsync** *(method)* - Where a mailbox stands: its own use and limit, the shared total or reserve and its use, and whether it is full - that is, whether it may receive or send.
- **Caps** *(property)* - The administrator's smaller limits, by mailbox.
- **HasShared** *(property)* - True when the plan has a shared total or reserve.
- **PersonBytes** *(property)* - Each mailbox's own size (person and mixed plans).
- **Plan** *(property)* - "none", "person", "shared" or "mixed".
- **SharedBytes** *(property)* - The shared total (shared plan) or the shared reserve (mixed plan).
