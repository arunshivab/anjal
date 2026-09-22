# SenderRules

**Namespace:** `Anjal.Spam`

Evaluates per-tenant sender allow/block rules.

## Members

- **Evaluate** *(method)* - Find the rule that applies to a sender. Exact-address rules beat domain rules; within the same specificity, block beats allow. Both the envelope sender and the From header address are checked.
- **IsValidPattern** *(method)* - Whether a rule pattern is well formed: local@domain or @domain, the domain containing a dot, with no whitespace or slashes. Shared by the admin API and the webmail so both accept exactly the same patterns.
