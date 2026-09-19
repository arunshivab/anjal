# SenderRules

**Namespace:** `Anjal.Spam`

Evaluates per-tenant sender allow/block rules.

## Members

- **Evaluate** *(method)* - Find the rule that applies to a sender. Exact-address rules beat domain rules; within the same specificity, block beats allow. Both the envelope sender and the From header address are checked.
